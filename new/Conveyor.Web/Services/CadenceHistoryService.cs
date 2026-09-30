using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace Conveyor.Web.Services;

public sealed record CadencePoint(DateTimeOffset At, double ParcelsPerHour);

public sealed class CadenceHistoryService(IConveyorSupervisor supervisor, ILogger<CadenceHistoryService> logger,
    IWebHostEnvironment? environment = null) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<CadencePoint> _points = [];
    private Dictionary<int, long>? _previous;
    private DateTimeOffset _start;
    private long _received;
    private bool _dirty;
    private string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Conveyor.Web", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(environment!.ContentRootPath).ToUpperInvariant())))[..16], "cadence-history.json");
    public event Action? Changed;
    public IReadOnlyList<CadencePoint> GetPoints() { lock (_gate) return _points.ToArray(); }

    internal void Observe(DateTimeOffset now, Dictionary<int, long> totals, bool countersReady = true)
    {
        lock (_gate)
        {
            if (_points.RemoveAll(p => p.At < now.AddHours(-24)) > 0) _dirty = true;
            // Restored totals are a baseline, never parcels received in this interval.
            if (!countersReady)
            {
                _previous = null;
                _received = 0;
                return;
            }
            // A counter reset or topology change starts a fresh complete interval.
            if (_previous is null || totals.Count != _previous.Count ||
                totals.Any(p => !_previous.TryGetValue(p.Key, out var old) || p.Value < old))
            {
                _previous = totals; _start = now; _received = 0;
                return;
            }
            _received += totals.Sum(p => p.Value - _previous[p.Key]);
            _previous = totals;
            var elapsed = now - _start;
            if (elapsed < TimeSpan.FromMinutes(10)) return;
            _points.Add(new(now, _received / elapsed.TotalHours));
            _dirty = true;
            _start = now; _received = 0;
        }
        Changed?.Invoke();
    }

    internal async Task LoadAsync(string path, DateTimeOffset now, CancellationToken token = default)
    {
        if (!File.Exists(path)) return;
        var points = JsonSerializer.Deserialize<List<CadencePoint>>(await File.ReadAllTextAsync(path, token))
            ?? throw new InvalidDataException("Historique de cadence invalide.");
        if (points.Any(p => p is null || !double.IsFinite(p.ParcelsPerHour) || p.ParcelsPerHour < 0))
            throw new InvalidDataException("Point de cadence invalide.");
        lock (_gate)
        {
            _points.Clear();
            _points.AddRange(points.Where(p => p.At >= now.AddHours(-24) && p.At <= now).OrderBy(p => p.At));
            _previous = null;
            _received = 0;
        }
        Changed?.Invoke();
    }

    internal async Task SaveAsync(string path, CancellationToken token = default)
    {
        CadencePoint[] points;
        lock (_gate)
        {
            if (!_dirty) return;
            points = _points.ToArray();
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(points), token);
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            lock (_gate) _dirty = true;
            throw;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await LoadAsync(FilePath, DateTimeOffset.UtcNow, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception exception) { logger.LogWarning(exception, "Historique de cadence illisible ; nouvelle collecte sans historique"); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    // Read readiness first: initialization may finish while snapshots are taken.
                    var ready = supervisor.CountersReady;
                    Observe(DateTimeOffset.UtcNow, ready ? supervisor.GetSnapshots().ToDictionary(l => l.LineId,
                        l => l.ProductionCounters?.TotalParcels ?? 0) : new(), ready);
                }
                catch (Exception exception)
                {
                    lock (_gate) _previous = null;
                    logger.LogWarning(exception, "Lecture de la cadence indisponible");
                }
                try { await SaveAsync(FilePath, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception) { logger.LogWarning(exception, "Sauvegarde de la cadence impossible ; nouvel essai dans cinq secondes"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
