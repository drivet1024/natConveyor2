using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

// Archive writes are independent of PLC processing and of the counter restoration path.
public sealed class SortingDayArchiveService(IOptions<ConveyorOptions> options, IConveyorSupervisor supervisor,
    CounterStatisticsService statistics, CadenceHistoryService cadence, ISortingDayArchiveStore store,
    IWebHostEnvironment environment, ILogger<SortingDayArchiveService> logger) : BackgroundService
{
    private readonly string _session = Guid.NewGuid().ToString("N");
    private int _resetGeneration;
    private void OnReset() => Interlocked.Increment(ref _resetGeneration);

    internal static SortingDaySnapshot Capture(ConveyorOptions configuration, IConveyorSupervisor supervisor,
        IReadOnlyList<CadencePoint> cadence, DateTimeOffset at, string session, int generation)
    {
        var shift = configuration.Statistics.GetShiftStart(at.LocalDateTime);
        var slot = SortingDaySnapshot.SlotFor(at);
        var lines = supervisor.GetSnapshots();
        var available = lines.Any(line => line.Running) && lines.Where(line => line.Running)
            .All(line => line.Connections.Plc && !line.Connections.Simulated);
        var result = new SortingDaySnapshot
        {
            Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{session}:{generation}:{shift.Ticks}:{slot.UtcTicks}")))[..32],
            DepotId = configuration.General!.DepotId, ConveyorId = configuration.General.ConveyorId ?? 0,
            ShiftStart = shift, Slot = slot, CapturedAt = at, Session = session, ResetGeneration = generation,
            SortingShiftId = supervisor.CurrentShiftId,
            ConveyorRunning = available ? supervisor.ConveyorRunning : null,
            FullChutesCount = available ? supervisor.FullChutesCount : null,
            Code42Count = available ? supervisor.Code42Count : null
        };
        foreach (var line in lines)
        {
            var dbLine = configuration.GetConfiguredLines().Single(option => option.Id == line.LineId).DatabaseLineId;
            result.Lines.Add(new(line.LineId, dbLine, false,
                (line.ProductionCounters ?? (line.Maintenance ? new() : line.Counters)).Copy()));
            result.Lines.Add(new(line.LineId, dbLine, true,
                (line.MaintenanceCounters ?? (line.Maintenance ? line.Counters : new())).Copy()));
        }
        var counts = supervisor.FullChuteTransitions;
        var durations = supervisor.FullChuteDurations;
        var full = supervisor.FullChutesActive.ToHashSet();
        result.Chutes = counts.Keys.Union(durations.Keys).Order().Select(chute => new SortingDayChute(chute,
            counts.GetValueOrDefault(chute), durations.GetValueOrDefault(chute)?.TotalSeconds,
            available && counts.GetValueOrDefault(chute).HasValue ? full.Contains(chute) : null)).ToList();
        result.Cadence = cadence.Where(point => configuration.Statistics.GetShiftStart(point.At.LocalDateTime) == shift)
            .Select(point => new SortingDayCadence(point.At, point.ParcelsPerHour)).ToList();
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Statistics.Enabled || options.Value.Simulation) return;
        var installation = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(environment.ContentRootPath).ToUpperInvariant())))[..16];
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Conveyor.Web", installation, "sorting-day-archive.json");
        var buffer = new SortingDayArchiveBuffer(path, store);
        supervisor.CountersReset += OnReset;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    await buffer.LoadAsync(stoppingToken);
                    var at = DateTimeOffset.Now;
                    var shift = options.Value.Statistics.GetShiftStart(at.LocalDateTime);
                    // Do not archive restored totals as activity, or previous-shift counters under a new date.
                    if (supervisor.CountersReady && statistics.ActiveShift == shift)
                    {
                        var generation = Volatile.Read(ref _resetGeneration);
                        var snapshot = Capture(options.Value, supervisor, cadence.GetPoints(), at, _session, generation);
                        if (statistics.ActiveShift == shift && generation == Volatile.Read(ref _resetGeneration))
                            await buffer.EnqueueAsync(snapshot, stoppingToken);
                    }
                    await buffer.FlushAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Archivage des journées de tri impossible ; nouvel essai dans cinq secondes");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { supervisor.CountersReset -= OnReset; }
    }
}

internal sealed class SortingDayArchiveBuffer(string path, ISortingDayArchiveStore store)
{
    private List<SortingDaySnapshot>? _pending;
    public async Task LoadAsync(CancellationToken token = default)
    {
        if (_pending is not null) return;
        _pending = File.Exists(path)
            ? JsonSerializer.Deserialize<List<SortingDaySnapshot>>(await File.ReadAllTextAsync(path, token))
                ?? throw new InvalidDataException("File d’archivage invalide.")
            : [];
    }
    public async Task EnqueueAsync(SortingDaySnapshot snapshot, CancellationToken token = default)
    {
        await LoadAsync(token);
        var next = _pending!.Where(old => old.Id != snapshot.Id).Append(snapshot).ToList();
        await PersistAsync(next, token);
        _pending = next;
    }
    public async Task FlushAsync(CancellationToken token = default)
    {
        await LoadAsync(token);
        while (_pending!.Count > 0)
        {
            await store.SaveAsync(_pending[0], token);
            var next = _pending.Skip(1).ToList();
            await PersistAsync(next, token);
            _pending = next;
        }
    }
    private async Task PersistAsync(List<SortingDaySnapshot> snapshots, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(snapshots), token);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
