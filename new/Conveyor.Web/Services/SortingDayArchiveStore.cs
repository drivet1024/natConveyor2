using System.Text.Json;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Conveyor.Web.Services;

public interface ISortingDayArchiveStore
{
    Task SaveAsync(SortingDaySnapshot snapshot, CancellationToken token);
    Task<IReadOnlyList<SortingDaySnapshot>> LoadAsync(DateTime day, CancellationToken token);
}

public sealed class SortingDayArchiveStore(IOptions<ConveyorOptions> options) : ISortingDayArchiveStore
{
    public const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS conveyor_day_snapshot (
          snapshot_id CHAR(32) NOT NULL PRIMARY KEY,
          depot_id INT NOT NULL, conveyor_id INT NOT NULL, shift_start DATETIME NOT NULL,
          sample_slot_utc DATETIME(6) NOT NULL, captured_at_utc DATETIME(6) NOT NULL,
          session_id CHAR(32) NOT NULL, reset_generation INT NOT NULL,
          source VARCHAR(24) NOT NULL, sorting_shift_id INT NULL,
          conveyor_running TINYINT NULL, full_chutes_count INT NULL, code42_count INT NULL,
          payload_json LONGTEXT NOT NULL,
          INDEX ix_day (depot_id, conveyor_id, shift_start, captured_at_utc)
        ) ENGINE=InnoDB;
        CREATE TABLE IF NOT EXISTS conveyor_day_metric (
          snapshot_id CHAR(32) NOT NULL, line_id INT NOT NULL, database_line_id INT NULL,
          maintenance TINYINT NOT NULL, metric VARCHAR(80) NOT NULL, value DECIMAL(30,8) NULL,
          PRIMARY KEY(snapshot_id,line_id,maintenance,metric)
        ) ENGINE=InnoDB;
        CREATE TABLE IF NOT EXISTS conveyor_day_chute (
          snapshot_id CHAR(32) NOT NULL, line_id INT NOT NULL, maintenance TINYINT NOT NULL,
          chute INT NOT NULL, dispatched BIGINT NULL, full_transitions BIGINT NULL,
          full_seconds DOUBLE NULL, full_active TINYINT NULL,
          PRIMARY KEY(snapshot_id,line_id,maintenance,chute)
        ) ENGINE=InnoDB;
        CREATE TABLE IF NOT EXISTS conveyor_day_cadence (
          depot_id INT NOT NULL, conveyor_id INT NOT NULL, shift_start DATETIME NOT NULL,
          measured_at_utc DATETIME(6) NOT NULL, parcels_per_hour DOUBLE NOT NULL,
          PRIMARY KEY(depot_id,conveyor_id,shift_start,measured_at_utc)
        ) ENGINE=InnoDB;
        """;

    private bool _schemaReady;
    public static async Task EnsureSchemaAsync(MySqlConnection connection, CancellationToken token)
    {
        await using var command = new MySqlCommand(SchemaSql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task SaveAsync(SortingDaySnapshot snapshot, CancellationToken token)
    {
        await using var connection = new MySqlConnection(options.Value.Database.ConnectionString);
        await connection.OpenAsync(token);
        if (!_schemaReady) { await EnsureSchemaAsync(connection, token); _schemaReady = true; }
        await using var tx = await connection.BeginTransactionAsync(token);
        await WriteSnapshotAsync(connection, tx, snapshot, token);
        await tx.CommitAsync(token);
    }

    public static async Task WriteSnapshotAsync(MySqlConnection connection, MySqlTransaction tx,
        SortingDaySnapshot snapshot, CancellationToken token)
    {
        await using var header = new MySqlCommand("""
            INSERT INTO conveyor_day_snapshot
            (snapshot_id,depot_id,conveyor_id,shift_start,sample_slot_utc,captured_at_utc,session_id,reset_generation,
             source,sorting_shift_id,conveyor_running,full_chutes_count,code42_count,payload_json)
            VALUES (@id,@depot,@conveyor,@shift,@slot,@at,@session,@reset,@source,@sortShift,@running,@full,@code42,@json)
            ON DUPLICATE KEY UPDATE captured_at_utc=@at, sorting_shift_id=@sortShift, conveyor_running=@running,
              full_chutes_count=@full, code42_count=@code42, payload_json=@json
            """, connection, tx);
        header.Parameters.AddWithValue("@id", snapshot.Id);
        header.Parameters.AddWithValue("@depot", snapshot.DepotId);
        header.Parameters.AddWithValue("@conveyor", snapshot.ConveyorId);
        header.Parameters.AddWithValue("@shift", snapshot.ShiftStart);
        header.Parameters.AddWithValue("@slot", snapshot.Slot.UtcDateTime);
        header.Parameters.AddWithValue("@at", snapshot.CapturedAt.UtcDateTime);
        header.Parameters.AddWithValue("@session", snapshot.Session);
        header.Parameters.AddWithValue("@reset", snapshot.ResetGeneration);
        header.Parameters.AddWithValue("@source", snapshot.Source);
        header.Parameters.AddWithValue("@sortShift", snapshot.SortingShiftId);
        header.Parameters.AddWithValue("@running", snapshot.ConveyorRunning);
        header.Parameters.AddWithValue("@full", snapshot.FullChutesCount);
        header.Parameters.AddWithValue("@code42", snapshot.Code42Count);
        header.Parameters.AddWithValue("@json", JsonSerializer.Serialize(snapshot));
        await header.ExecuteNonQueryAsync(token);
        foreach (var table in new[] { "conveyor_day_metric", "conveyor_day_chute" })
        {
            await using var clear = new MySqlCommand($"DELETE FROM {table} WHERE snapshot_id=@id", connection, tx);
            clear.Parameters.AddWithValue("@id", snapshot.Id);
            await clear.ExecuteNonQueryAsync(token);
        }
        async Task InsertRows(string table, string columns, IEnumerable<object?[]> rows, string suffix = "")
        {
            foreach (var batch in rows.Chunk(200))
            {
                await using var command = new MySqlCommand { Connection = connection, Transaction = tx };
                var tuples = new List<string>();
                foreach (var row in batch)
                {
                    var names = new List<string>();
                    foreach (var value in row)
                    {
                        var name = "@p" + command.Parameters.Count;
                        names.Add(name);
                        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                    }
                    tuples.Add("(" + string.Join(",", names) + ")");
                }
                command.CommandText = $"INSERT INTO {table} ({columns}) VALUES {string.Join(",", tuples)} {suffix}";
                await command.ExecuteNonQueryAsync(token);
            }
        }
        await InsertRows("conveyor_day_metric", "snapshot_id,line_id,database_line_id,maintenance,metric,value",
            snapshot.Lines.SelectMany(line => SortingDaySnapshot.Metrics(line).Select(pair =>
                new object?[] { snapshot.Id, line.LineId, line.DatabaseLineId, line.Maintenance, pair.Key, pair.Value })));
        await InsertRows("conveyor_day_chute", "snapshot_id,line_id,maintenance,chute,dispatched,full_transitions,full_seconds,full_active",
            snapshot.Lines.SelectMany(line => line.Counters.ChuteDispatchCounts.Select(chute =>
                new object?[] { snapshot.Id, line.LineId, line.Maintenance, chute.Key, chute.Value, null, null, null }))
            .Concat(snapshot.Chutes.Select(chute => new object?[]
                { snapshot.Id, -1, false, chute.Chute, null, chute.FullTransitions, chute.FullSeconds, chute.Full })));
        await InsertRows("conveyor_day_cadence", "depot_id,conveyor_id,shift_start,measured_at_utc,parcels_per_hour",
            snapshot.Cadence.Select(point => new object?[]
                { snapshot.DepotId, snapshot.ConveyorId, snapshot.ShiftStart, point.At.UtcDateTime, point.ParcelsPerHour }),
            "ON DUPLICATE KEY UPDATE parcels_per_hour=VALUES(parcels_per_hour)");
    }

    public async Task<IReadOnlyList<SortingDaySnapshot>> LoadAsync(DateTime day, CancellationToken token)
    {
        if (options.Value.Simulation) return [];
        await using var connection = new MySqlConnection(options.Value.Database.ConnectionString);
        await connection.OpenAsync(token);
        List<SortingDaySnapshot> snapshots = [];
        try
        {
            await using var command = new MySqlCommand("""
                SELECT payload_json FROM conveyor_day_snapshot
                WHERE depot_id=@depot AND conveyor_id=@conveyor AND shift_start>=@day AND shift_start<@end
                ORDER BY captured_at_utc
                """, connection);
            command.Parameters.AddWithValue("@depot", options.Value.General!.DepotId);
            command.Parameters.AddWithValue("@conveyor", options.Value.General.ConveyorId ?? 0);
            command.Parameters.AddWithValue("@day", day.Date);
            command.Parameters.AddWithValue("@end", day.Date.AddDays(1));
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) snapshots.Add(JsonSerializer.Deserialize<SortingDaySnapshot>(reader.GetString(0))
                ?? throw new InvalidDataException("Archive invalide."));
        }
        catch (MySqlException exception) when (exception.Number == 1146) { }
        // Existing exact counters remain available without an invasive historical backfill.
        var grouped = new Dictionary<DateTime, SortingDaySnapshot>();
        foreach (var line in options.Value.GetConfiguredLines())
        {
            await using var legacy = new MySqlCommand("""
                SELECT shift_start,mode,counters_json FROM conveyor_counter_state
                WHERE depot_id=@depot AND line_id=@line AND shift_start>=@day AND shift_start<@end AND mode IN (0,2)
                """, connection);
            legacy.Parameters.AddWithValue("@depot", options.Value.General!.DepotId);
            legacy.Parameters.AddWithValue("@line", line.DatabaseLineId ?? 0);
            legacy.Parameters.AddWithValue("@day", day.Date);
            legacy.Parameters.AddWithValue("@end", day.Date.AddDays(1));
            await using var reader = await legacy.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var shift = reader.GetDateTime(0);
                if (!grouped.TryGetValue(shift, out var snapshot))
                    grouped[shift] = snapshot = new() { DepotId = options.Value.General.DepotId,
                        ConveyorId = options.Value.General.ConveyorId ?? 0, ShiftStart = shift, Source = "legacy", CapturedAt = new DateTimeOffset(shift) };
                snapshot.Lines.Add(SortingDaySnapshot.FromLegacy(line.Id, line.DatabaseLineId, reader.GetInt32(1) == 2, reader.GetString(2)));
            }
        }
        return grouped.Values.OrderBy(s => s.ShiftStart).Concat(snapshots).ToArray();
    }
}
