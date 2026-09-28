using System.Collections.Frozen;
using Conveyor.Web.Domain;

namespace Conveyor.Web.Services;

public sealed class ShipmentPrefixCache(IConveyorRepository repository, ILogger<ShipmentPrefixCache> logger) : BackgroundService
{
    private FrozenDictionary<string, int?[]> _prefixes = new Dictionary<string, int?[]>().ToFrozenDictionary(StringComparer.Ordinal);

    public bool Matches(string barcode) => barcode.Length is 11 or 12 && Volatile.Read(ref _prefixes).ContainsKey(barcode[..3]);

    public MissingShipmentAttribution? Identify(IEnumerable<string> barcodes)
    {
        var lookup = Volatile.Read(ref _prefixes);
        var matched = barcodes.Where(code => code.Length is 11 or 12)
            .Select(code => code[..3]).Where(lookup.ContainsKey).Distinct().Order().ToArray();
        if (matched.Length == 0) return null;
        var customers = matched.SelectMany(prefix => lookup[prefix]).Distinct().Order()
            .Select(id => id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Inconnu");
        return new(string.Join(", ", customers), string.Join(", ", matched));
    }

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
            var prefixes = (await repository.GetShipmentCustomerPrefixesAsync(token))
                .Where(row => row.Prefix.Length == 3).GroupBy(row => row.Prefix)
                .ToFrozenDictionary(group => group.Key, group => group.Select(row => row.CustomerId).Distinct().ToArray(), StringComparer.Ordinal);
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
