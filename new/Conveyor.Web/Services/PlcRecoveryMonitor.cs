using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

public sealed class PlcRecoveryMonitor(IConveyorSupervisor supervisor, IOptions<ConveyorOptions> options,
    ISmsAlerts sms, ILogger<PlcRecoveryMonitor> logger) : BackgroundService
{
    private readonly PlcRecoveryIncident _incident = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do
            {
                var primary = supervisor.GetSnapshots().FirstOrDefault();
                await _incident.CheckAsync(DateTimeOffset.UtcNow,
                    !options.Value.Simulation && primary?.Running == true && !supervisor.RslinxRestartInProgress,
                    primary?.Connections.Plc == true, options.Value.RslinxRestart.AutoRestart,
                    () => supervisor.RestartRslinxAutomaticallyAsync(stoppingToken), message => sms.Notify(message),
                    exception => logger.LogError(exception, "Reprise automatique RSLinx échouée ; aucune nouvelle tentative pour cet incident"));
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

internal sealed class PlcRecoveryIncident
{
    private DateTimeOffset? _disconnectedSince;
    private DateTimeOffset? _healthySince;
    private DateTimeOffset _nextRestart;
    private bool _alerted;
    private bool _attempted;

    public async Task CheckAsync(DateTimeOffset now, bool requested, bool healthy, bool autoRestart,
        Func<Task<string>> restart, Action<string> notify, Action<Exception> logError)
    {
        if (!requested)
        {
            _disconnectedSince = null; _healthySince = null; _alerted = false; _attempted = false;
            return;
        }
        if (healthy)
        {
            _disconnectedSince = null;
            _healthySince ??= now;
            if (_alerted && now - _healthySince >= TimeSpan.FromSeconds(30))
            {
                notify("Communication automate rétablie et stable depuis 30 secondes");
                _alerted = false; _attempted = false;
            }
            return;
        }
        _healthySince = null;
        _disconnectedSince ??= now;
        if (now - _disconnectedSince < TimeSpan.FromSeconds(15)) return;
        if (!_alerted)
        {
            _alerted = true;
            notify("Automate déconnecté depuis au moins 15 secondes");
        }
        if (!autoRestart || _attempted || now < _nextRestart) return;
        _attempted = true;
        _nextRestart = now.AddMinutes(5);
        notify("Redémarrage automatique RSLinx demandé après déconnexion automate");
        try { notify(await restart()); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logError(exception);
            notify("Échec du redémarrage automatique RSLinx ; intervention requise, consulter les journaux");
        }
    }
}
