using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Conveyor.Web.Services;

public interface ICounterStatisticsStore
{
    Task SaveAsync(CounterStatistics statistics, CancellationToken token);
}

public sealed class CounterStatisticsStore(IOptions<ConveyorOptions> options) : ICounterStatisticsStore
{
    public async Task SaveAsync(CounterStatistics statistics, CancellationToken token)
    {
        var (table, perLine) = GetDestination(statistics.Destination);
        if (perLine && statistics.LineId <= 0) throw new InvalidOperationException("Statistiques : ID de ligne MySQL manquant dans la capture locale.");
        await using var connection = new MySqlConnection(options.Value.Database.ConnectionString);
        await connection.OpenAsync(token);
        // One application writes this local database. Checking the shift also makes a retry
        // safe if the INSERT succeeded but its response or the local acknowledgement was lost.
        await using var existing = new MySqlCommand($"SELECT ID FROM {table} WHERE DEPOT_ID=@depot AND {(perLine ? "line_id=@line AND " : "")}INSERT_DATE=@date LIMIT 1", connection);
        existing.Parameters.AddWithValue("@depot", statistics.DepotId);
        existing.Parameters.AddWithValue("@line", statistics.LineId);
        existing.Parameters.AddWithValue("@date", statistics.ShiftStartedAt);
        if (await existing.ExecuteScalarAsync(token) is not null) return;

        await using var command = new MySqlCommand($"""
            INSERT INTO {table}
                (DEPOT_ID, {(perLine ? "line_id, " : "")}NB_SCANNED, NB_REJECTED, PC_REJECTED, NB_RECYCLED, PC_RECYCLED,
                 INSERT_DATE, PC_FULLCHUTE, PC_CODE98, PC_CODE42, PC_CODE68, NB_SORTED)
            VALUES (@depot, {(perLine ? "@line, " : "")}@scanned, @rejected, @pcRejected, @recycled, @pcRecycled,
                    @date, NULL, @pc98, NULL, @pc68, @sorted)
            """, connection);
        command.Parameters.AddWithValue("@depot", statistics.DepotId);
        command.Parameters.AddWithValue("@line", statistics.LineId);
        command.Parameters.AddWithValue("@date", statistics.ShiftStartedAt);
        command.Parameters.AddWithValue("@scanned", checked((int)statistics.Scanned));
        command.Parameters.AddWithValue("@rejected", checked((int)statistics.Rejected));
        command.Parameters.AddWithValue("@recycled", checked((int)statistics.Recycled));
        command.Parameters.AddWithValue("@sorted", checked((int)statistics.Sorted));
        command.Parameters.AddWithValue("@pcRejected", statistics.RejectedPercent);
        command.Parameters.AddWithValue("@pcRecycled", statistics.RecycledPercent);
        command.Parameters.AddWithValue("@pc98", statistics.Code98Percent);
        command.Parameters.AddWithValue("@pc68", statistics.Code68Percent);
        await command.ExecuteNonQueryAsync(token);
    }

    internal static (string Table, bool PerLine) GetDestination(StatisticsDestination destination) => destination switch
    {
        StatisticsDestination.ProductionLine => ("conveyor_stats_dde", true),
        StatisticsDestination.ProductionGlobal => ("conveyor_stats_dde_global", false),
        StatisticsDestination.Maintenance => ("conveyor_stats_dde_maintenance", true),
        _ => throw new InvalidOperationException("Destination de statistiques inconnue.")
    };
}
