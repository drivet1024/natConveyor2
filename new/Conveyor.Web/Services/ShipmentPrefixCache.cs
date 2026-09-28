using System.Collections.Frozen;

namespace Conveyor.Web.Services;

public sealed class ShipmentPrefixCache(IConveyorRepository repository, ILogger<ShipmentPrefixCache> logger) : BackgroundService
{
    private FrozenSet<string> _prefixes = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);

    public bool Matches(string barcode) => barcode.Length is 11 or 12 && Volatile.Read(ref _prefixes).Contains(barcode[..3]);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Warm the shared cache before the conveyor supervisor starts processing parcels.
        await RefreshAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    internal async Task RefreshAsync(CancellationToken token)
    {
        try
        {
            var prefixes = (await repository.GetShipmentPrefixesAsync(token))
                .Where(prefix => prefix.Length == 3).ToFrozenSet(StringComparer.Ordinal);
            Volatile.Write(ref _prefixes, prefixes);
            logger.LogInformation("Préfixes du compteur Pas dans le système chargés : {Count} préfixes, depuis le 2026-09-20, hors 518", prefixes.Count);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Chargement des préfixes impossible : liste précédente conservée ({Count} préfixes). Sans liste disponible, aucun colis ne sera compté Pas dans le système", Volatile.Read(ref _prefixes).Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RefreshAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
