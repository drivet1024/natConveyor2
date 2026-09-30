using System.Text.Json;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Conveyor.Web.Services;

public sealed record StatisticsHistoryPoint(DateTime ShiftStart, long TotalParcels,
    double BalanceErrorPercent, double? DimensionErrorPercent, long? LightParcels, long? SmallParcels)
{
    public double? SortedPercent { get; init; }
    public double? ShipmentNotFoundPercent { get; init; }
    public double? RouteNotConfiguredPercent { get; init; }
    public double? MultipleBarcodesPercent { get; init; }
    public double? Code98Percent { get; init; }
    public double? Code68Percent { get; init; }
    public double? NoReadPercent { get; init; }
    public double? LightParcelPercent { get; init; }
    public double? SmallParcelPercent { get; init; }
    public double? InverseDimensionPercent { get; init; }
    public long? ScaleFaults { get; init; }
    public decimal? TotalVolumeCubicFeet { get; init; }
    public long VolumeMeasuredParcels { get; init; }
    public decimal? AverageVolumeCubicFeet => VolumeMeasuredParcels > 0 ? TotalVolumeCubicFeet / VolumeMeasuredParcels : null;
    public decimal? AverageWeightPounds { get; init; }
    public long WeightMeasuredParcels { get; init; }
}

public sealed class StatisticsHistoryService(IOptions<ConveyorOptions> options)
{
    public static (DateTime From, DateTime To) GetThirtyDayWindow(DateTime today)
    {
        var to = today.Date.AddDays(1);
        return (to.AddDays(-30), to);
    }

    public Task<IReadOnlyList<StatisticsHistoryPoint>> LoadGlobalAsync(CancellationToken token = default) =>
        LoadAsync("conveyor_stats_dde_global", null, false, token);

    public Task<IReadOnlyList<StatisticsHistoryPoint>> LoadMaintenanceAsync(int? lineId,
        CancellationToken token = default) => LoadAsync("conveyor_stats_dde_maintenance", lineId, true, token);

    private async Task<IReadOnlyList<StatisticsHistoryPoint>> LoadAsync(string table, int? lineId,
        bool filterByLine, CancellationToken token)
    {
        var configuration = options.Value;
        if (configuration.Simulation || string.IsNullOrWhiteSpace(configuration.Database.ConnectionString)) return [];
        var (from, to) = GetThirtyDayWindow(DateTime.Now);
        var lineFilter = filterByLine ? " AND line_id <=> @line" : "";
        var sql = $"""
            SELECT INSERT_DATE, COALESCE(NB_SCANNED, 0), COALESCE(PC_WEIGHT_ERROR, 0),
                   NB_LIGHT_PARCEL, NB_SMALL_PARCEL, NB_SORTED, PC_CODE98, PC_CODE68,
                   NB_INVERSE_LENGHT_PARCEL, NB_SCALE_ERROR
            FROM {table}
            WHERE DEPOT_ID=@depot AND INSERT_DATE>=@from AND INSERT_DATE<@to{lineFilter}
            ORDER BY INSERT_DATE
            """;
        await using var connection = new MySqlConnection(configuration.Database.ConnectionString);
        await connection.OpenAsync(token);
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@depot", configuration.General!.DepotId);
        command.Parameters.AddWithValue("@from", from);
        command.Parameters.AddWithValue("@to", to);
        if (filterByLine) command.Parameters.AddWithValue("@line", lineId is null ? DBNull.Value : lineId.Value);
        List<(DateTime ShiftStart, long TotalParcels, double BalanceErrorPercent,
            long? LightParcels, long? SmallParcels, long? Sorted, double? Code98Percent,
            double? Code68Percent, long? InverseDimensions, long? ScaleFaults)> rows = [];
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add((reader.GetDateTime(0), reader.GetInt64(1), ClampPercent(reader.GetDouble(2)),
                NullableInt64(reader, 3), NullableInt64(reader, 4), NullableInt64(reader, 5),
                NullablePercent(reader, 6), NullablePercent(reader, 7), NullableInt64(reader, 8),
                NullableInt64(reader, 9)));
        await reader.DisposeAsync();

        // Detailed counters contain the newer metrics that do not exist in the legacy
        // public statistics tables. Preserve genuinely unavailable history as missing.
        var detailedCounters = await LoadDetailedCountersAsync(connection, configuration.General!.DepotId,
            filterByLine ? StatisticsDestination.Maintenance : StatisticsDestination.ProductionLine,
            filterByLine ? lineId ?? 0 : null, from, to, token);
        return rows.Select(row =>
        {
            detailedCounters.TryGetValue(row.ShiftStart, out var counters);
            var total = row.TotalParcels;
            var eligible = counters is null ? 0 : Math.Max(0, counters.TotalParcels - counters.NoReads);
            return new StatisticsHistoryPoint(row.ShiftStart, total, row.BalanceErrorPercent,
                counters is null ? null : Percentage(counters.DimensionErrors, eligible),
                row.LightParcels, row.SmallParcels)
            {
                SortedPercent = counters is null
                    ? Percentage(row.Sorted, total)
                    : Percentage(counters.SortedByWaybill + counters.SortedByPostalCode, total),
                ShipmentNotFoundPercent = counters is null ? null : Percentage(counters.RejectedShipmentNotFound, total),
                RouteNotConfiguredPercent = counters is null ? null : Percentage(counters.RejectedRouteNotConfigured, total),
                MultipleBarcodesPercent = counters is null ? null : Percentage(counters.RejectedMultipleShipments, total),
                Code98Percent = counters is null ? row.Code98Percent : Percentage(counters.Code98, total),
                Code68Percent = counters is null ? row.Code68Percent : Percentage(counters.Code68, total),
                NoReadPercent = counters is null ? null : Percentage(counters.NoReads, total),
                LightParcelPercent = counters is null ? Percentage(row.LightParcels, total) : Percentage(counters.LightParcels, total),
                SmallParcelPercent = counters is null ? Percentage(row.SmallParcels, total) : Percentage(counters.SmallParcels, total),
                InverseDimensionPercent = counters is null ? Percentage(row.InverseDimensions, total) : Percentage(counters.InverseLengthParcels, total),
                ScaleFaults = counters?.ScaleFaults ?? row.ScaleFaults,
                TotalVolumeCubicFeet = counters?.MeasuredVolumeCubicFeet,
                VolumeMeasuredParcels = counters?.VolumeMeasuredParcels ?? 0,
                AverageWeightPounds = counters is { WeightMeasuredParcels: > 0 }
                    ? counters.MeasuredWeightPounds / counters.WeightMeasuredParcels : null,
                WeightMeasuredParcels = counters?.WeightMeasuredParcels ?? 0
            };
        }).ToArray();
    }

    private static async Task<Dictionary<DateTime, LineCounters>> LoadDetailedCountersAsync(MySqlConnection connection,
        int depotId, StatisticsDestination destination, int? storedLineId, DateTime from, DateTime to,
        CancellationToken token)
    {
        var lineFilter = storedLineId.HasValue ? " AND line_id=@stateLine" : "";
        var grouped = new Dictionary<DateTime, List<LineCounters>>();
        try
        {
            await using var command = new MySqlCommand($"""
                SELECT shift_start, counters_json FROM conveyor_counter_state
                WHERE depot_id=@depot AND mode=@mode AND shift_start>=@from AND shift_start<@to{lineFilter}
                ORDER BY shift_start
                """, connection);
            command.Parameters.AddWithValue("@depot", depotId);
            command.Parameters.AddWithValue("@mode", (int)destination);
            command.Parameters.AddWithValue("@from", from);
            command.Parameters.AddWithValue("@to", to);
            if (storedLineId.HasValue) command.Parameters.AddWithValue("@stateLine", storedLineId.Value);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var counters = JsonSerializer.Deserialize<LineCounters>(reader.GetString(1));
                if (counters is null) continue;
                var shift = reader.GetDateTime(0);
                if (!grouped.TryGetValue(shift, out var values)) grouped[shift] = values = [];
                values.Add(counters);
            }
        }
        catch (MySqlException exception) when (exception.Number == 1146) { return []; }
        return grouped.ToDictionary(group => group.Key,
            group => CounterStatistics.CaptureCombined(depotId, group.Key, group.Value).Counters!);
    }

    private static long? NullableInt64(MySqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static double? NullablePercent(MySqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ClampPercent(reader.GetDouble(ordinal));

    private static double? Percentage(long? count, long total) => count.HasValue && total > 0
        ? ClampPercent(100d * count.Value / total) : count.HasValue ? 0 : null;

    internal static double CalculateDimensionErrorPercent(IEnumerable<LineCounters> lines)
    {
        var counters = lines.ToArray();
        var eligible = counters.Sum(line => Math.Max(0, line.TotalParcels - line.NoReads));
        var errors = counters.Sum(line => line.DimensionErrors);
        return ClampPercent(eligible == 0 ? 0 : 100d * errors / eligible);
    }

    private static double ClampPercent(double value) => Math.Clamp(value, 0, 100);
}
