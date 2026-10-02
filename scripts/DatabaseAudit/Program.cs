using Microsoft.Extensions.Configuration;
using MySqlConnector;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Conveyor.Web.Services;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] is not ("--apply-archive-schema" or "--verify-archive")))
    throw new ArgumentException("Usage: DatabaseAudit <chemin conveyor.settings.json> [--apply-archive-schema|--verify-archive]");
var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(args[0])).Build();
var settings = new MySqlConnectionStringBuilder(configuration["Conveyor:Database:ConnectionString"] ?? throw new InvalidOperationException("Connexion MySQL absente."))
{ ConnectionTimeout = 5, DefaultCommandTimeout = 15 };
Console.WriteLine($"Cible : {settings.Server}:{settings.Port}/{settings.Database}");
try
{
    await using var connection = new MySqlConnection(settings.ConnectionString);
    await connection.OpenAsync();
    if (args.ElementAtOrDefault(1) == "--apply-archive-schema")
    {
        await SortingDayArchiveStore.EnsureSchemaAsync(connection, CancellationToken.None);
        Console.WriteLine("Tables d’archives créées ou déjà présentes. Aucune table existante modifiée.");
    }
    if (args.ElementAtOrDefault(1) == "--verify-archive")
    {
        await using (var tx = await connection.BeginTransactionAsync())
        {
            var sample = new SortingDaySnapshot
            {
                DepotId = -1, ConveyorId = -1, ShiftStart = new(2000, 1, 1),
                CapturedAt = DateTimeOffset.UtcNow, Slot = SortingDaySnapshot.SlotFor(DateTimeOffset.UtcNow),
                Session = Guid.NewGuid().ToString("N"),
                Lines = [new(0, null, false, new() { TotalParcels = 100, NoReads = 2, ChuteDispatchCounts = new() { [4] = 98 } }),
                         new(0, null, true, new() { TotalParcels = 3 })],
                Chutes = [new(4, 2, 15.5, true)],
                Cadence = [new(new DateTimeOffset(2000, 1, 1, 0, 10, 0, TimeSpan.Zero), 600)]
            };
            await SortingDayArchiveStore.WriteSnapshotAsync(connection, tx, sample, CancellationToken.None);
            sample.Lines[0].Counters.TotalParcels = 101;
            await SortingDayArchiveStore.WriteSnapshotAsync(connection, tx, sample, CancellationToken.None);
            await using var check = new MySqlCommand("SELECT value FROM conveyor_day_metric WHERE snapshot_id=@id AND line_id=0 AND maintenance=0 AND metric='TotalParcels'", connection, tx);
            check.Parameters.AddWithValue("@id", sample.Id);
            if (Convert.ToDecimal(await check.ExecuteScalarAsync()) != 101m) throw new InvalidOperationException("Échec de l’upsert des compteurs.");
            await tx.RollbackAsync();
            Console.WriteLine("Insertion et mise à jour validées ; transaction de test annulée, aucun relevé de test conservé.");
        }
        var options = configuration.GetSection("Conveyor").Get<ConveyorOptions>()!;
        options.ApplyGlobalSorting();
        options.Simulation = false;
        await using var latest = new MySqlCommand("SELECT depot_id, MAX(shift_start) FROM conveyor_counter_state GROUP BY depot_id ORDER BY MAX(shift_start) DESC LIMIT 1", connection);
        DateTime historyDay;
        await using (var latestReader = await latest.ExecuteReaderAsync())
        {
            if (!await latestReader.ReadAsync()) throw new InvalidOperationException("Aucun historique existant à vérifier.");
            options.General!.DepotId = latestReader.GetInt32(0);
            historyDay = latestReader.GetDateTime(1).Date;
        }
        var archive = new SortingDayArchiveStore(Microsoft.Extensions.Options.Options.Create(options));
        var samples = await archive.LoadAsync(historyDay, CancellationToken.None);
        if (samples.Count == 0) throw new InvalidOperationException("Échec de lecture de l’historique existant.");
        Console.WriteLine($"Lecture de l’historique existant : {samples.Count} relevé(s), {samples.Sum(s => s.Lines.Count)} ensembles de compteurs.");
        return;
    }
    await using var command = new MySqlCommand("""
        SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE FROM information_schema.COLUMNS
        WHERE TABLE_SCHEMA=DATABASE() AND
        (TABLE_NAME LIKE 'conveyor%stat%' OR TABLE_NAME LIKE 'conveyor%counter%'
         OR TABLE_NAME LIKE '%cadence%' OR TABLE_NAME LIKE 'conveyor%day%')
        ORDER BY TABLE_NAME,ORDINAL_POSITION
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync()) Console.WriteLine($"{reader.GetString(0)} | {reader.GetString(1)} | {reader.GetString(2)}");
    await reader.CloseAsync();
    foreach (var table in new[] { "conveyor_stats_dde", "conveyor_stats_dde_global", "conveyor_stats_dde_maintenance", "conveyor_counter_state" })
    {
        var date = table == "conveyor_counter_state" ? "shift_start" : "INSERT_DATE";
        await using var summary = new MySqlCommand($"SELECT depot_id, line_id, COUNT(*), MIN({date}), MAX({date}) FROM {table} GROUP BY depot_id,line_id", connection);
        await using var rows = await summary.ExecuteReaderAsync();
        while (await rows.ReadAsync()) Console.WriteLine($"Résumé {table} : dépôt={rows.GetValue(0)}, ligne={rows.GetValue(1)}, lignes={rows.GetValue(2)}, du={rows.GetValue(3)}, au={rows.GetValue(4)}");
    }
}
catch (MySqlException exception)
{
    Console.Error.WriteLine($"Audit MySQL impossible (code {exception.Number}) : {exception.Message}");
    Environment.ExitCode = 1;
}
