using System.Text.Json;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Conveyor.Web.Services;

public sealed record StatisticsHistoryPoint(DateTime ShiftStart, long TotalParcels,
    double BalanceErrorPercent, double? DimensionErrorPercent, long? LightParcels, long? SmallParcels);

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
                   NB_LIGHT_PARCEL, NB_SMALL_PARCEL
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
            long? LightParcels, long? SmallParcels)> rows = [];
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add((reader.GetDateTime(0), reader.GetInt64(1), ClampPercent(reader.GetDouble(2)),
                reader.IsDBNull(3) ? null : reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)));
        await reader.DisposeAsync();

        // Dimension errors were not part of the original public statistics tables.
        // Merge the exact detailed counters and preserve missing history as missing.
        var dimensionRates = await LoadDimensionRatesAsync(connection, configuration.General!.DepotId,
            filterByLine ? StatisticsDestination.Maintenance : StatisticsDestination.ProductionLine,
            filterByLine ? lineId ?? 0 : null, from, to, token);
        return rows.Select(row => new StatisticsHistoryPoint(row.ShiftStart, row.TotalParcels,
            row.BalanceErrorPercent, dimensionRates.GetValueOrDefault(row.ShiftStart),
            row.LightParcels, row.SmallParcels)).ToArray();
    }

    private static async Task<Dictionary<DateTime, double?>> LoadDimensionRatesAsync(MySqlConnection connection,
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
            group => (double?)CalculateDimensionErrorPercent(group.Value));
    }

    internal static double CalculateDimensionErrorPercent(IEnumerable<LineCounters> lines)
    {
        var counters = lines.ToArray();
        var eligible = counters.Sum(line => Math.Max(0, line.TotalParcels - line.NoReads));
        var errors = counters.Sum(line => line.DimensionErrors);
        return ClampPercent(eligible == 0 ? 0 : 100d * errors / eligible);
    }

    private static double ClampPercent(double value) => Math.Clamp(value, 0, 100);
}
