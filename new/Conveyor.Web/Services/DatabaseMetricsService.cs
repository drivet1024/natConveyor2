using Conveyor.Web.Domain;

namespace Conveyor.Web.Services;

public sealed class DatabaseMetricsService(IConveyorRepository repository, ILogger<DatabaseMetricsService> logger)
    : BackgroundService, IDatabaseMetricsService
{
    private DateTimeOffset _lastShipmentCheck = DateTimeOffset.MinValue;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private long? _cachedPostalCodes;
    private DatabaseReferenceCounts _current = new(0, 0, 0, false, repository.IsSimulation, DateTimeOffset.MinValue);
    public event Action? Changed;
    public DatabaseReferenceCounts Current { get { lock (_gate) return _current; } }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => RefreshCoreAsync(false, cancellationToken);
    public Task RefreshAfterResetAsync(CancellationToken cancellationToken = default) => RefreshCoreAsync(true, cancellationToken);

    private async Task RefreshCoreAsync(bool afterReset, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            if (afterReset) _cachedPostalCodes = null;
            await ReadCountsAsync(cancellationToken);
        }
        finally { _refreshGate.Release(); }
        Changed?.Invoke();
    }

    private async Task ReadCountsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var counts = await repository.GetReferenceCountsAsync(cancellationToken, _cachedPostalCodes);
            // Keep polling while empty; once repopulated, freeze until the next Reset Data.
            if (counts.PostalCodes > 0) _cachedPostalCodes = counts.PostalCodes;
            var lastUpdate = Current.LastShipmentUpdate;
            if (counts.Parcels == 0) lastUpdate = null;
            else if (Current.Parcels == 0 || DateTimeOffset.UtcNow - _lastShipmentCheck >= TimeSpan.FromSeconds(30))
            {
                _lastShipmentCheck = DateTimeOffset.UtcNow;
                try { lastUpdate = await repository.GetLastShipmentUpdateAsync(cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { logger.LogWarning(exception, "Impossible de lire la date de mise à jour des colis"); }
            }
            bool? recentShipmentUpdates = null;
            try { recentShipmentUpdates = await repository.HasRecentShipmentUpdatesAsync(cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogWarning(exception, "Impossible de vérifier la synchronisation des colis dans les 60 dernières minutes"); }
            lock (_gate) _current = new(counts.Parcels, counts.PostalCodes, counts.Scans, true, repository.IsSimulation, DateTimeOffset.Now, counts.HasOverdueScans, lastUpdate, recentShipmentUpdates);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_gate) _current = _current with { Connected = false, UpdatedAt = DateTimeOffset.Now };
            logger.LogWarning(exception, "Impossible de lire les compteurs de la base locale");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await RefreshAsync(stoppingToken);
    }
}
