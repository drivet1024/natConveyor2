using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

// Device processing remains independent from disk and SQL writes.
public sealed class CounterStatisticsService(IOptions<ConveyorOptions> options, ICounterStatisticsStore store,
    IWebHostEnvironment environment, ILogger<CounterStatisticsService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StatisticsState? _state;
    private DateTime? _activeShift;
    private DateTime _nextAttempt;
    private string? _filePath;
    internal string? FallbackDirectoryOverride { get; set; }
    public bool Initialized => _activeShift.HasValue;
    private string LegacyFilePath => Path.Combine(environment.ContentRootPath, "data", "counter-statistics.json");
    private string FallbackFilePath
    {
        get
        {
            var root = FallbackDirectoryOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Conveyor.Web");
            var installation = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(environment.ContentRootPath).ToUpperInvariant())))[..16];
            return Path.Combine(root, installation, "counter-statistics.json");
        }
    }
    private string FilePath => _filePath ??= File.Exists(FallbackFilePath) ? FallbackFilePath : LegacyFilePath;

    private async Task LoadStateAsync(CancellationToken token)
    {
        _state ??= File.Exists(FilePath)
            ? JsonSerializer.Deserialize<StatisticsState>(await File.ReadAllTextAsync(FilePath, token))
                ?? throw new InvalidOperationException("État des sauvegardes statistiques invalide.")
            : new StatisticsState();
    }

    public async Task InitializeAsync(DateTime now, Action<int, LineCounters, LineCounters> restore, CancellationToken token)
    {
        if (Initialized) return;
        await _gate.WaitAsync(token);
        try
        {
            if (Initialized) return;
            await LoadStateAsync(token);
            var configuration = options.Value;
            var shift = configuration.Statistics.GetShiftStart(now);
            var restored = new List<(int Id, LineCounters Production, LineCounters Maintenance)>();
            foreach (var line in configuration.GetConfiguredLines())
            {
                async Task<LineCounters> Load(StatisticsDestination destination)
                {
                    var pending = _state!.Pending.LastOrDefault(row => row.DepotId == configuration.General!.DepotId
                        && row.LineId == line.DatabaseLineId && row.ShiftStartedAt == shift && row.Destination == destination);
                    if (pending is not null) return pending.RestoreCounters();
                    return await store.LoadAsync(configuration.General!.DepotId, line.DatabaseLineId, shift, destination, token);
                }
                restored.Add((line.Id, await Load(StatisticsDestination.ProductionLine), await Load(StatisticsDestination.Maintenance)));
            }
            // Apply only after every read succeeds: retries cannot partially reset running counters.
            foreach (var line in restored) restore(line.Id, line.Production, line.Maintenance);
            _activeShift = shift;
        }
        finally { _gate.Release(); }
    }

    public async Task TickAsync(DateTime now, Func<IReadOnlyList<LineSnapshot>> snapshots, CancellationToken token,
        Action? resetForNewShift = null)
    {
        var configuration = options.Value;
        if (!configuration.Statistics.Enabled || configuration.Simulation) return;
        if (!Initialized) throw new InvalidOperationException("Restaurer les compteurs avant leur sauvegarde.");
        await _gate.WaitAsync(token);
        try
        {
            var shift = configuration.Statistics.GetShiftStart(now);
            // Preserve the previous shift before resetting either mode, even if MySQL is offline.
            await CapturePendingAsync(_activeShift!.Value, snapshots(), token);
            if (shift > _activeShift.Value)
            {
                if (resetForNewShift is null) throw new InvalidOperationException("Remise à zéro du shift non configurée.");
                resetForNewShift();
                _activeShift = shift;
                await CapturePendingAsync(shift, snapshots(), token);
            }
            if (now < _nextAttempt) return;
            try
            {
                foreach (var statistics in _state!.Pending.ToArray())
                {
                    await store.SaveAsync(statistics, token);
                    var next = new StatisticsState { Pending = _state.Pending.Skip(1).ToList() };
                    await PersistAsync(next, token);
                    _state = next;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _nextAttempt = now.AddSeconds(5);
                logger.LogError(exception, "Synchronisation des statistiques échouée ; nouvel essai dans cinq secondes");
            }
        }
        finally { _gate.Release(); }
    }

    private async Task CapturePendingAsync(DateTime shift, IReadOnlyList<LineSnapshot> snapshots, CancellationToken token)
    {
        var captured = Capture(options.Value, snapshots.ToDictionary(line => line.LineId), shift);
        var next = new StatisticsState { Pending = [.. _state!.Pending] };
        foreach (var row in captured)
        {
            next.Pending.RemoveAll(old => old.DepotId == row.DepotId && old.LineId == row.LineId
                && old.ShiftStartedAt == row.ShiftStartedAt && old.Destination == row.Destination);
            next.Pending.Add(row);
        }
        await PersistAsync(next, token);
        _state = next;
    }

    internal static CounterStatistics[] Capture(ConveyorOptions configuration, IReadOnlyDictionary<int, LineSnapshot> snapshots, DateTime shiftStart)
    {
        var lines = configuration.GetConfiguredLines().ToArray();
        var production = lines.Select(line => snapshots[line.Id].ProductionCounters
            ?? (snapshots[line.Id].Maintenance ? new LineCounters() : snapshots[line.Id].Counters)).ToArray();
        var maintenance = lines.Select(line => snapshots[line.Id].MaintenanceCounters
            ?? (snapshots[line.Id].Maintenance ? snapshots[line.Id].Counters : new LineCounters())).ToArray();
        var maintenanceActive = lines.Any(line => snapshots[line.Id].Maintenance);
        List<CounterStatistics> result = [];
        if (!maintenanceActive || production.Any(counters => counters.TotalParcels > 0))
        {
            for (var i = 0; i < lines.Length; i++)
                result.Add(CounterStatistics.Capture(configuration.General!.DepotId,
                    lines[i].DatabaseLineId,
                    shiftStart, production[i]));
            result.Add(CounterStatistics.CaptureCombined(configuration.General!.DepotId, shiftStart, production));
        }
        if (maintenanceActive || maintenance.Any(counters => counters.TotalParcels > 0))
            for (var i = 0; i < lines.Length; i++)
                result.Add(CounterStatistics.Capture(configuration.General!.DepotId,
                    lines[i].DatabaseLineId,
                    shiftStart, maintenance[i]) with { Destination = StatisticsDestination.Maintenance });
        return result.ToArray();
    }

    private async Task PersistAsync(StatisticsState state, CancellationToken token)
    {
        var path = FilePath;
        var json = JsonSerializer.Serialize(state);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, token);
        try
        {
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (UnauthorizedAccessException) when (path == LegacyFilePath)
        {
            // A deployed application directory may permit creating the temporary file
            // but deny replacement of the existing state file.
            var fallback = FallbackFilePath;
            await WriteAtomicallyAsync(fallback, json, token);
            _filePath = fallback;
            logger.LogWarning("Sauvegarde locale des statistiques déplacée vers {Path} (dossier de l’application protégé)", fallback);
        }
    }

    private static async Task WriteAtomicallyAsync(string path, string json, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, token);
        File.Move(temporaryPath, path, overwrite: true);
    }

    public sealed class StatisticsState
    {
        public DateTime LastCapturedAt { get; set; } = DateTime.MinValue;
        public List<CounterStatistics> Pending { get; set; } = [];
    }
}
