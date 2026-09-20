using System.Collections.Concurrent;
using System.Net;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

public sealed class ConverterResetService(IOptions<ConveyorOptions> options, ILogger<ConverterResetService> logger)
{
    private readonly ConcurrentDictionary<string, byte> _busy = new();

    public async Task ResetAsync(int lineId, bool scale)
    {
        var line = options.Value.GetConfiguredLines().Single(line => line.Id == lineId);
        var converter = scale ? line.ScaleConverter : line.DimensionConverter;
        var device = scale ? "Balance" : "Dimensionneur";
        if (!IPAddress.TryParse(converter.IpAddress.Trim(), out var address))
            throw new InvalidOperationException("Configurer l’adresse IP du convertisseur dans les paramètres de cette ligne.");
        if (options.Value.Simulation) throw new InvalidOperationException("Reset matériel désactivé en mode simulation.");
        var host = address.ToString();
        if (!_busy.TryAdd(host, 0)) throw new InvalidOperationException("Un reset est déjà en cours pour ce convertisseur.");
        try
        {
            using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false, AllowAutoRedirect = true };
            using var client = new HttpClient(handler) { BaseAddress = new UriBuilder("http", host).Uri, Timeout = TimeSpan.FromSeconds(30) };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            logger.LogInformation("Ligne {Line} : reset {Device}, convertisseur {Type} à {Host}", lineId, device, converter.Type, host);
            await SendResetAsync(client, converter.Type, timeout.Token);
            logger.LogInformation("Ligne {Line} : commande de reset {Device} acceptée; en attente de reconnexion de l’appareil", lineId, device);
        }
        catch (Exception exception)
        {
            // Do not log request URLs: old converter authentication uses query parameters.
            logger.LogWarning("Ligne {Line} : échec reset {Device} à {Host} ({Error})", lineId, device, host, exception.GetType().Name);
            throw new InvalidOperationException("Reset non confirmé. Vérifier l’adresse, le modèle du convertisseur et sa disponibilité.");
        }
        finally { _busy.TryRemove(host, out _); }
    }

    public static async Task SendResetAsync(HttpClient client, string type, CancellationToken token)
    {
        if (type is not ("Old" or "New")) throw new InvalidOperationException("Modèle de convertisseur invalide.");
        // Same factory credentials and endpoints as the legacy SerialConverter.
        using var login = type == "Old"
            ? await client.GetAsync("cgi/login.cgi?Username=admin&Password=system", token)
            : await client.PostAsync("index.htm", new FormUrlEncodedContent(new Dictionary<string, string>
                { ["username"] = "admin", ["password"] = "admin", ["Login"] = "Login" }), token);
        login.EnsureSuccessStatusCode();
        var html = await login.Content.ReadAsStringAsync(token);
        if (html.Contains("Invalid User", StringComparison.OrdinalIgnoreCase) || (type == "Old" && string.IsNullOrWhiteSpace(html)))
            throw new InvalidOperationException("Authentification convertisseur refusée.");
        // 'ture' is intentional: preserve the legacy device endpoint spelling.
        using var reset = await client.GetAsync(type == "Old" ? "cgi/reset.cgi?back=Reset&reset=ture" : "msgreboot.htm", token);
        reset.EnsureSuccessStatusCode();
        if ((await reset.Content.ReadAsStringAsync(token)).Contains("Invalid User", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Reset convertisseur refusé.");
    }
}
