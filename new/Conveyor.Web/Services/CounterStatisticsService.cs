using System.Text.Json;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

// Called by the supervisor before daily counter resets. Device processing stays independent.
public sealed class CounterStatisticsService(IOptions<ConveyorOptions> options, ICounterStatisticsStore store,
    IWebHostEnvironment environment, ILogger<CounterStatisticsService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StatisticsState? _state;
    private DateTime? _firstTick;
    private DateTime _nextAttempt;
    private string FilePath => Path.Combine(environment.ContentRootPath, "data", "counter-statistics.json");

    public async Task TickAsync(DateTime now, Func<IReadOnlyList<LineSnapshot>> snapshots, CancellationToken token,
        Action? afterCapture = null)
    {
        var configuration = options.Value;
        if (!configuration.Statistics.Enabled || configuration.Simulation) return;
        await _gate.WaitAsync(token);
        try
        {
            _firstTick ??= now;
            _state ??= File.Exists(FilePath)
                ? JsonSerializer.Deserialize<StatisticsState>(await File.ReadAllTextAsync(FilePath, token))
                    ?? throw new InvalidOperationException("État des sauvegardes statistiques invalide.")
                : new StatisticsState();
            var scheduledAt = now.Date + configuration.Statistics.SaveTime.ToTimeSpan();
            if (scheduledAt > now) scheduledAt = scheduledAt.AddDays(-1);
            // Do not attribute newly restarted, empty counters to an already missed shift.
            if (now >= scheduledAt && _firstTick <= scheduledAt && _state.LastCapturedAt < scheduledAt)
            {
                var current = snapshots().ToDictionary(line => line.LineId);
                var statistics = Capture(configuration, current, configuration.Statistics.GetShiftStart(scheduledAt));
                var next = new StatisticsState
                {
                    LastCapturedAt = scheduledAt,
                    Pending = [.. _state.Pending, .. statistics]
                };
                // Persist first: a failed disk write must prevent the daily reset.
                await PersistAsync(next, token);
                _state = next;
                foreach (var captured in statistics)
                    logger.LogInformation("Statistiques {Destination} capturées localement : dépôt {Depot}, ligne MySQL {Line}, début shift {Shift}, {Scanned} scans",
                        captured.Destination, captured.DepotId, captured.LineId, captured.ShiftStartedAt, captured.Scanned);
            }

            // The daily reset follows the durable capture, before potentially slow SQL calls.
            afterCapture?.Invoke();
            if (now < _nextAttempt || _state.Pending.Count == 0) return;
            try
            {
                foreach (var statistics in _state.Pending.ToArray())
                {
                    await store.SaveAsync(statistics, token);
                    var next = new StatisticsState
                    {
                        LastCapturedAt = _state.LastCapturedAt,
                        Pending = _state.Pending.Skip(1).ToList()
                    };
                    await PersistAsync(next, token);
                    _state = next;
                    logger.LogInformation("Statistiques {Destination} enregistrées : dépôt {Depot}, ligne MySQL {Line}, début shift {Shift}",
                        statistics.Destination, statistics.DepotId, statistics.LineId, statistics.ShiftStartedAt);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _nextAttempt = now.AddMinutes(1);
                logger.LogError(exception, "Sauvegarde statistiques MySQL échouée ; copie locale conservée, nouvel essai dans une minute");
            }
        }
        finally { _gate.Release(); }
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
                    lines[i].DatabaseLineId ?? throw new InvalidOperationException("Configurer l’ID de ligne MySQL pour sauvegarder les statistiques."),
                    shiftStart, production[i]));
            result.Add(CounterStatistics.CaptureCombined(configuration.General!.DepotId, shiftStart, production));
        }
        if (maintenanceActive || maintenance.Any(counters => counters.TotalParcels > 0))
            for (var i = 0; i < lines.Length; i++)
                result.Add(CounterStatistics.Capture(configuration.General!.DepotId,
                    lines[i].DatabaseLineId ?? throw new InvalidOperationException("Configurer l’ID de ligne MySQL pour sauvegarder les statistiques de maintenance."),
                    shiftStart, maintenance[i]) with { Destination = StatisticsDestination.Maintenance });
        return result.ToArray();
    }

    private async Task PersistAsync(StatisticsState state, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(state), token);
        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    public sealed class StatisticsState
    {
        public DateTime LastCapturedAt { get; set; } = DateTime.MinValue;
        public List<CounterStatistics> Pending { get; set; } = [];
    }
}
