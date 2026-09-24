using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

public interface ISmsAlerts
{
    void Notify(string action, int? lineId = null);
}

// The bounded queue prevents a Twilio outage from blocking equipment commands.
public sealed class SmsAlerts(IOptions<ConveyorOptions> options, IHttpClientFactory clients,
    ILogger<SmsAlerts> logger) : BackgroundService, ISmsAlerts
{
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(200)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait
    });

    public void Notify(string action, int? lineId = null)
    {
        var config = options.Value;
        if (!config.Sms.Enabled) return;
        if (config.Simulation)
        {
            logger.LogInformation("SMS simulé : {Action}, ligne {Line}; aucun envoi Twilio", action, lineId + 1);
            return;
        }
        var general = config.General;
        var line = lineId.HasValue ? $" ligne [{lineId.Value + 1}]" : " toutes lignes";
        var message = $"WARN: {action} [{general?.Name}] dépôt [{general?.GetDepotDisplayName() ?? "Nom non configuré"}] convoyeur [{general?.ConveyorId}]{line} {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}";
        if (!_queue.Writer.TryWrite(message))
            logger.LogWarning("SMS non mis en file : file pleine ou service arrêté ({Action})", action);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sms = options.Value.Sms;
        if (!sms.Enabled || options.Value.Simulation) return;
        if (sms.ValidationError() is { } error)
        {
            logger.LogError("Alertes SMS désactivées : {Error}", error);
            _queue.Writer.TryComplete();
            return;
        }
        try
        {
            await foreach (var message in _queue.Reader.ReadAllAsync(stoppingToken))
                foreach (var recipient in sms.GetRecipients())
                    await SendAsync(sms, recipient, message, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SendAsync(SmsOptions sms, string recipient, string message, CancellationToken token)
    {
        try
        {
            using var client = clients.CreateClient("TwilioSms");
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://api.twilio.com/2010-04-01/Accounts/{sms.AccountSid.Trim()}/Messages.json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{sms.AccountSid.Trim()}:{sms.AuthToken.Trim()}")));
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["From"] = sms.FromNumber.Trim(), ["To"] = recipient, ["Body"] = message
            });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await client.SendAsync(request, timeout.Token);
            // Never log credentials, full phone numbers or the provider response body.
            if (response.IsSuccessStatusCode)
                logger.LogInformation("SMS accepté par Twilio (livraison non confirmée)");
            else
                logger.LogWarning("SMS refusé par Twilio : HTTP {Status}; consulter la console Twilio", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Do not retry an ambiguous timeout: Twilio may already have accepted the SMS.
            logger.LogWarning("Échec d’envoi SMS ({ErrorType}); les commandes convoyeur restent exécutées", exception.GetType().Name);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        // Drain pending operational alerts before cancellation, within the host shutdown budget.
        if (ExecuteTask is { } task)
        {
            try { await task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        await base.StopAsync(cancellationToken);
    }
}
