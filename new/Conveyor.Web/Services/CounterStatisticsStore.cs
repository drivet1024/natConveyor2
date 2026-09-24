using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;
using MySqlConnector;
using System.Text.Json;

namespace Conveyor.Web.Services;

public interface ICounterStatisticsStore
{
    Task SaveAsync(CounterStatistics statistics, CancellationToken token);
    Task<LineCounters> LoadAsync(int depotId, int lineId, DateTime shiftStart, StatisticsDestination destination, CancellationToken token);
}

public sealed class CounterStatisticsStore(IOptions<ConveyorOptions> options) : ICounterStatisticsStore
{
    private bool _schemaReady;

    private async Task EnsureSchemaAsync(MySqlConnection connection, CancellationToken token)
    {
        if (_schemaReady) return;
        await using var exists = new MySqlCommand("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='conveyor_counter_state'", connection);
        if (Convert.ToInt64(await exists.ExecuteScalarAsync(token)) > 0)
        {
            _schemaReady = true;
            return;
        }
        await using var command = new MySqlCommand("""
            CREATE TABLE IF NOT EXISTS conveyor_counter_state (
                depot_id INT NOT NULL, line_id INT NOT NULL, shift_start DATETIME NOT NULL,
                mode INT NOT NULL, counters_json LONGTEXT NOT NULL,
                PRIMARY KEY (depot_id, line_id, shift_start, mode)
            ) ENGINE=InnoDB
            """, connection);
        await command.ExecuteNonQueryAsync(token);
        _schemaReady = true;
    }

    public async Task<LineCounters> LoadAsync(int depotId, int lineId, DateTime shiftStart,
        StatisticsDestination destination, CancellationToken token)
    {
        await using var connection = new MySqlConnection(options.Value.Database.ConnectionString);
        await connection.OpenAsync(token);
        await EnsureSchemaAsync(connection, token);
        await using var exact = new MySqlCommand("SELECT counters_json FROM conveyor_counter_state WHERE depot_id=@depot AND line_id=@line AND shift_start=@date AND mode=@mode", connection);
        exact.Parameters.AddWithValue("@depot", depotId);
        exact.Parameters.AddWithValue("@line", lineId);
        exact.Parameters.AddWithValue("@date", shiftStart);
        exact.Parameters.AddWithValue("@mode", (int)destination);
        if (await exact.ExecuteScalarAsync(token) is string json)
            return JsonSerializer.Deserialize<LineCounters>(json) ?? throw new InvalidOperationException("Compteurs sauvegardés invalides.");

        // Compatibility with shifts saved before the detailed counter table existed.
        var (table, _) = GetDestination(destination);
        await using var legacy = new MySqlCommand($"SELECT NB_SCANNED, NB_REJECTED, NB_RECYCLED, NB_SORTED, PC_CODE98, PC_CODE68, NB_WEIGHT_ERROR, NB_SCALE_ERROR FROM {table} WHERE DEPOT_ID=@depot AND line_id=@line AND INSERT_DATE=@date ORDER BY ID DESC LIMIT 1", connection);
        legacy.Parameters.AddWithValue("@depot", depotId);
        legacy.Parameters.AddWithValue("@line", lineId);
        legacy.Parameters.AddWithValue("@date", shiftStart);
        await using var reader = await legacy.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return new();
        long Count(int index) => reader.IsDBNull(index) ? 0 : reader.GetInt64(index);
        var scanned = Count(0);
        long FromPercent(int index) => reader.IsDBNull(index) ? 0 : (long)Math.Round(scanned * reader.GetDouble(index) / 100d);
        return new() { TotalParcels = scanned, CameraReads = scanned, Rejected = Count(1), Code97 = Count(2),
            SortedByWaybill = Count(3), Code98 = FromPercent(4), Code68 = FromPercent(5),
            ScaleErrors = Count(6), ScaleFaults = Count(7) };
    }

    public async Task SaveAsync(CounterStatistics statistics, CancellationToken token)
    {
        var (table, perLine) = GetDestination(statistics.Destination);
        if (perLine && statistics.LineId <= 0) throw new InvalidOperationException("Statistiques : ID de ligne MySQL manquant dans la capture locale.");
        await using var connection = new MySqlConnection(options.Value.Database.ConnectionString);
        await connection.OpenAsync(token);
        await EnsureSchemaAsync(connection, token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        // One application writes this local database. Checking the shift also makes a retry
        // safe if the INSERT succeeded but its response or the local acknowledgement was lost.
        await using var existing = new MySqlCommand($"SELECT ID FROM {table} WHERE DEPOT_ID=@depot AND {(perLine ? "line_id=@line AND " : "")}INSERT_DATE=@date ORDER BY ID DESC LIMIT 1 FOR UPDATE", connection, transaction);
        existing.Parameters.AddWithValue("@depot", statistics.DepotId);
        existing.Parameters.AddWithValue("@line", statistics.LineId);
        existing.Parameters.AddWithValue("@date", statistics.ShiftStartedAt);
        var id = await existing.ExecuteScalarAsync(token);

        await using var command = new MySqlCommand(id is not null ? $"""
            UPDATE {table} SET NB_SCANNED=@scanned, NB_REJECTED=@rejected, PC_REJECTED=@pcRejected,
                NB_RECYCLED=@recycled, PC_RECYCLED=@pcRecycled, PC_CODE98=@pc98, PC_CODE68=@pc68,
                NB_SORTED=@sorted, NB_WEIGHT_ERROR=@weightErrors, PC_WEIGHT_ERROR=@pcWeightErrors,
                NB_SCALE_ERROR=@scaleErrors, PC_SCALE_ERROR=@pcScaleErrors WHERE ID=@id
            """ : $"""
            INSERT INTO {table}
                (DEPOT_ID, {(perLine ? "line_id, " : "")}NB_SCANNED, NB_REJECTED, PC_REJECTED, NB_RECYCLED, PC_RECYCLED,
                 INSERT_DATE, PC_FULLCHUTE, PC_CODE98, PC_CODE42, PC_CODE68, NB_SORTED,
                 NB_WEIGHT_ERROR, PC_WEIGHT_ERROR, NB_SCALE_ERROR, PC_SCALE_ERROR)
            VALUES (@depot, {(perLine ? "@line, " : "")}@scanned, @rejected, @pcRejected, @recycled, @pcRecycled,
                    @date, NULL, @pc98, NULL, @pc68, @sorted,
                    @weightErrors, @pcWeightErrors, @scaleErrors, @pcScaleErrors)
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
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
        command.Parameters.AddWithValue("@weightErrors", checked((int)statistics.WeightErrors));
        command.Parameters.AddWithValue("@pcWeightErrors", statistics.WeightErrorPercent);
        command.Parameters.AddWithValue("@scaleErrors", checked((int)statistics.ScaleErrors));
        command.Parameters.AddWithValue("@pcScaleErrors", statistics.ScaleErrorPercent);
        await command.ExecuteNonQueryAsync(token);
        if (perLine && statistics.Counters is not null)
        {
            await using var detail = new MySqlCommand("""
                INSERT INTO conveyor_counter_state (depot_id, line_id, shift_start, mode, counters_json)
                VALUES (@depot, @line, @date, @mode, @json)
                ON DUPLICATE KEY UPDATE counters_json=@json
                """, connection, transaction);
            detail.Parameters.AddWithValue("@depot", statistics.DepotId);
            detail.Parameters.AddWithValue("@line", statistics.LineId);
            detail.Parameters.AddWithValue("@date", statistics.ShiftStartedAt);
            detail.Parameters.AddWithValue("@mode", (int)statistics.Destination);
            detail.Parameters.AddWithValue("@json", JsonSerializer.Serialize(statistics.Counters));
            await detail.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    internal static (string Table, bool PerLine) GetDestination(StatisticsDestination destination) => destination switch
    {
        StatisticsDestination.ProductionLine => ("conveyor_stats_dde", true),
        StatisticsDestination.ProductionGlobal => ("conveyor_stats_dde_global", false),
        StatisticsDestination.Maintenance => ("conveyor_stats_dde_maintenance", true),
        _ => throw new InvalidOperationException("Destination de statistiques inconnue.")
    };
}
