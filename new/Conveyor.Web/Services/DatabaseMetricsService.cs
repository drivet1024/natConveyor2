using Conveyor.Web.Domain;

namespace Conveyor.Web.Services;

public sealed class DatabaseMetricsService(IConveyorRepository repository, ILogger<DatabaseMetricsService> logger)
    : BackgroundService, IDatabaseMetricsService
{
    private DateTimeOffset _lastShipmentCheck = DateTimeOffset.MinValue;
    private readonly object _gate = new();
    private DatabaseReferenceCounts _current = new(0, 0, 0, false, repository.IsSimulation, DateTimeOffset.MinValue);
    public event Action? Changed;
    public DatabaseReferenceCounts Current { get { lock (_gate) return _current; } }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var counts = await repository.GetReferenceCountsAsync(cancellationToken);
            var lastUpdate = Current.LastShipmentUpdate;
            if (counts.Parcels == 0) lastUpdate = null;
            else if (Current.Parcels == 0 || DateTimeOffset.UtcNow - _lastShipmentCheck >= TimeSpan.FromSeconds(30))
            {
                _lastShipmentCheck = DateTimeOffset.UtcNow;
                try { lastUpdate = await repository.GetLastShipmentUpdateAsync(cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { logger.LogWarning(exception, "Impossible de lire la date de mise à jour des colis"); }
            }
            lock (_gate) _current = new(counts.Parcels, counts.PostalCodes, counts.Scans, true, repository.IsSimulation, DateTimeOffset.Now, counts.HasOverdueScans, lastUpdate);
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
