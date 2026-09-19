using Conveyor.Web.Domain;

namespace Conveyor.Web.Services;

public sealed class DatabaseMetricsService(IConveyorRepository repository, ILogger<DatabaseMetricsService> logger)
    : BackgroundService, IDatabaseMetricsService
{
    private readonly object _gate = new();
    private DatabaseReferenceCounts _current = new(0, 0, 0, false, repository.IsSimulation, DateTimeOffset.MinValue);
    public event Action? Changed;
    public DatabaseReferenceCounts Current { get { lock (_gate) return _current; } }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var counts = await repository.GetReferenceCountsAsync(cancellationToken);
            lock (_gate) _current = new(counts.Parcels, counts.PostalCodes, counts.Scans, true, repository.IsSimulation, DateTimeOffset.Now, counts.HasOverdueScans);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate) _current = _current with { Connected = false, UpdatedAt = DateTimeOffset.Now };
            logger.LogWarning(exception, "Impossible de lire les compteurs de la base locale");
        }
        Changed?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RefreshAsync(stoppingToken);
    }
}
