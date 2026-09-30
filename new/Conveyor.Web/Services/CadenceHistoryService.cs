namespace Conveyor.Web.Services;

public sealed record CadencePoint(DateTimeOffset At, double ParcelsPerHour);

public sealed class CadenceHistoryService(IConveyorSupervisor supervisor, ILogger<CadenceHistoryService> logger) : BackgroundService
{
    private readonly object _gate = new();
    private readonly List<CadencePoint> _points = [];
    private Dictionary<int, long>? _previous;
    private DateTimeOffset _start;
    private long _received;
    public event Action? Changed;
    public IReadOnlyList<CadencePoint> GetPoints() { lock (_gate) return _points.ToArray(); }

    internal void Observe(DateTimeOffset now, Dictionary<int, long> totals)
    {
        lock (_gate)
        {
            _points.RemoveAll(p => p.At < now.AddHours(-24));
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
            _start = now; _received = 0;
        }
        Changed?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                try
                {
                    Observe(DateTimeOffset.UtcNow, supervisor.GetSnapshots().ToDictionary(l => l.LineId,
                        l => l.ProductionCounters?.TotalParcels ?? 0));
                }
                catch (Exception exception)
                {
                    lock (_gate) _previous = null;
                    logger.LogWarning(exception, "Lecture de la cadence indisponible");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
