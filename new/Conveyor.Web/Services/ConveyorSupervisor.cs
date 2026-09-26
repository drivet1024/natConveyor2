using Conveyor.Web.Domain;
using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

public sealed class ConveyorSupervisor : BackgroundService, IConveyorSupervisor
{
    private readonly Dictionary<int, LineController> _lines;
    private readonly HashSet<int> _autoStartIds;
    private readonly IPlcGateway _plc;
    private readonly IConfigurationEditor _editor;
    private readonly SemaphoreSlim _motionGate = new(1, 1);
    private readonly SemaphoreSlim _shiftGate = new(1, 1);
    private readonly ConveyorOptions _configuration;
    private readonly IConveyorRepository _repository;
    private readonly ILogger _logger;
    private readonly ISmsAlerts? _sms;
    private readonly CounterStatisticsService? _statistics;
    private readonly bool _coordinateStatistics;
    private readonly IRslinxRestarter? _rslinxRestarter;
    private readonly string _closeChute39Tag;
    private readonly string _motionTag;
    public int CurrentShiftId => _configuration.General!.ShiftId;
    public bool? ConveyorRunning { get; private set; }
    public bool Maintenance => _configuration.General?.Maintenance == true;
    public bool HasStartedOperatingMode { get; private set; }
    private volatile bool _rslinxRestartInProgress;
    public bool RslinxRestartInProgress => _rslinxRestartInProgress;
    public bool CanChangeOperatingMode => ConveyorRunning == false && _plc.IsConnected
        && (_plc is not IPlcReadback readback || readback.ReadsHealthy);

    public async Task<ConveyorActionResult> SetConveyorMotionAsync(bool start, int? cause, bool maintenance = false)
    {
        if (start) EnsureCountersReady();
        if (!await _motionGate.WaitAsync(0)) throw new InvalidOperationException("Une commande convoyeur est déjà en cours.");
        try
        {
            if (start && (maintenance || Maintenance) && !CanChangeOperatingMode)
                throw new InvalidOperationException("Arrêter le convoyeur et attendre la confirmation d’arrêt de l’automate avant de démarrer en maintenance ou de revenir en production.");
            var modeSaved = true;
            var result = await ConveyorMotion.ExecuteAsync(_plc, _repository, _configuration.General?.ConveyorId,
                _configuration.Simulation, start, cause, _logger, _motionTag, async () =>
                {
                    if (!start) return;
                    _configuration.General!.Maintenance = maintenance;
                    HasStartedOperatingMode = true;
                    foreach (var line in _lines.Values) line.SetMaintenance(maintenance);
                    try { await _editor.SaveMaintenanceAsync(maintenance); }
                    catch (Exception exception)
                    {
                        modeSaved = false;
                        _logger.LogError(exception, "Mode {Mode} actif mais non enregistré pour le prochain redémarrage", maintenance ? "maintenance" : "production");
                    }
                });
            var action = start ? "Commande démarrer convoyeur envoyée" : $"Commande arrêter convoyeur envoyée ({cause switch { 0 => "PAUSE", 1 => "JAM", _ => "DOWN" }})";
            _sms?.Notify(action + (Maintenance ? " — maintenance" : " — production") + (result.Recorded ? "" : " ; échec enregistrement MySQL"));
            return result with { Message = result.Message + (start ? (maintenance ? " Mode maintenance actif." : " Mode production actif.") : "")
                + (modeSaved ? "" : " Attention : mode non mémorisé pour le prochain redémarrage.") };
        }
        finally { _motionGate.Release(); Changed?.Invoke(); }
    }

    public async Task SetShiftAsync(int shiftId)
    {
        var shifts = await _repository.GetShiftsAsync(CancellationToken.None);
        if (!shifts.Any(shift => shift.Id == shiftId))
            throw new InvalidOperationException("Ce shift n’est pas disponible dans les routes configurées.");
        await _shiftGate.WaitAsync();
        try
        {
            await _editor.SaveShiftAsync(shiftId);
            foreach (var line in _configuration.Lines) line.ShiftId = shiftId;
            _configuration.General!.ShiftId = shiftId;
            _logger.LogInformation("Dépôt 2 : shift {Shift} sélectionné pour les deux lignes", shiftId);
            Changed?.Invoke();
        }
        finally { _shiftGate.Release(); }
    }

    public event Action? Changed;

    public ConveyorSupervisor(IOptions<ConveyorOptions> options, IConveyorRepository repository,
        SortEngine sortEngine, ILoggerFactory loggerFactory, IConfigurationEditor editor, ISmsAlerts? sms = null,
        CounterStatisticsService? statistics = null, IRslinxRestarter? rslinxRestarter = null)
    {
        var configuration = options.Value;
        _configuration = configuration;
        _editor = editor;
        _sms = sms;
        _statistics = statistics;
        _rslinxRestarter = rslinxRestarter;
        _coordinateStatistics = configuration.Statistics.Enabled && !configuration.Simulation;
        if (_coordinateStatistics && statistics is null)
            throw new InvalidOperationException("Service de sauvegarde des statistiques manquant.");
        _repository = repository;
        _logger = loggerFactory.CreateLogger<ConveyorSupervisor>();
        var activeLines = configuration.GetConfiguredLines().ToArray();
        var primaryLine = activeLines.First();
        _closeChute39Tag = primaryLine.Plc.CloseChute39Tag;
        _motionTag = configuration.General?.ConveyorStartTag?.Trim() ?? ConveyorMotion.DefaultMotionTag;
        PlcConfiguration.Validate(primaryLine.Plc);
        var monitoredTags = activeLines.SelectMany(line => new[] { line.Plc.ChuteTag, line.Plc.TransferTag, line.Plc.ScaleFaultTag })
            .Append(_closeChute39Tag).Append(_motionTag).Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray();
        _plc = configuration.Simulation
            ? new SimulationPlcGateway(loggerFactory.CreateLogger<SimulationPlcGateway>())
            : string.Equals(primaryLine.Plc.Protocol, "Tcp", StringComparison.OrdinalIgnoreCase)
                ? new TcpPlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<TcpPlcGateway>())
                : string.Equals(primaryLine.Plc.Protocol, "OpcDa", StringComparison.OrdinalIgnoreCase)
                    ? new OpcDaPlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<OpcDaPlcGateway>(), monitoredTags)
                    : new DdePlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<DdePlcGateway>(), monitoredTags);
        var logger = loggerFactory.CreateLogger<ConveyorSupervisor>();
        foreach (var line in activeLines.Where(line => !line.Enabled))
            logger.LogInformation("Ligne {Line} : démarrage automatique désactivé; appareils non connectés jusqu’au START", line.Id + 1);
        _autoStartIds = activeLines.Where(line => line.Enabled).Select(line => line.Id).ToHashSet();
        _lines = activeLines.ToDictionary(line => line.Id, line =>
        {
            var controller = new LineController(line, configuration.Simulation, repository, _plc,
                line.Id == primaryLine.Id, sortEngine,
                loggerFactory.CreateLogger($"Conveyor.Line.{line.Id}"), () => Changed?.Invoke(), sms,
                automaticCounterReset: !_coordinateStatistics);
            controller.SetMaintenance(Maintenance);
            return controller;
        });
        if (_plc is IPlcReadback readback) readback.TagChanged += RecordPlcTagChange;
    }

    internal void RecordPlcTagChange(string tag, string value)
    {
        if (!string.IsNullOrWhiteSpace(_closeChute39Tag) && string.Equals(tag, _closeChute39Tag, StringComparison.OrdinalIgnoreCase))
        {
            var state = value.Trim('\0', ' ', '\r', '\n', '\t');
            if (state is "0" or "1")
                foreach (var line in _lines.Values) line.SetChute39Closed(state == "1");
        }
        if (string.Equals(tag, _motionTag, StringComparison.OrdinalIgnoreCase))
        {
            var state = value.Trim() switch { "1" => true, "0" => false, _ => (bool?)null };
            if (state != ConveyorRunning)
            {
                ConveyorRunning = state;
                Changed?.Invoke();
            }
        }
        foreach (var line in _configuration.GetConfiguredLines().Where(line => string.Equals(line.Plc.ChuteTag, tag, StringComparison.OrdinalIgnoreCase)))
            Get(line.Id).RecordPlcReception(value);
        foreach (var line in _configuration.GetConfiguredLines().Where(line => string.Equals(line.Plc.TransferTag, tag, StringComparison.OrdinalIgnoreCase)))
            Get(line.Id).RecordPlcTransferReception(value);
        foreach (var line in _configuration.GetConfiguredLines().Where(line => !string.IsNullOrWhiteSpace(line.Plc.ScaleFaultTag) && string.Equals(line.Plc.ScaleFaultTag, tag, StringComparison.OrdinalIgnoreCase)))
            Get(line.Id).RecordScaleFaultReception(value);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (_coordinateStatistics && !_statistics!.Initialized && !stoppingToken.IsCancellationRequested)
        {
            try { await _statistics.InitializeAsync(DateTime.Now, (id, production, maintenance) => Get(id).RestoreCounters(production, maintenance), stoppingToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Restauration des compteurs impossible ; démarrage des appareils différé, nouvel essai dans cinq secondes");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        foreach (var line in _lines.Where(pair => _autoStartIds.Contains(pair.Key)).Select(pair => pair.Value))
            await line.StartAsync();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            do
            {
                if (!_coordinateStatistics) continue;
                var now = DateTime.Now;
                try
                {
                    await _statistics!.TickAsync(now, GetSnapshots, stoppingToken, () =>
                    {
                        foreach (var line in _lines.Values) line.RestoreCounters(new(), new());
                    });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception, "Capture des statistiques impossible ; remise à zéro quotidienne différée");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_lines.Values.Select(line => line.StopAsync()));
        await base.StopAsync(cancellationToken);
        if (_coordinateStatistics && _statistics!.Initialized)
        {
            try
            {
                await _statistics.TickAsync(DateTime.Now, GetSnapshots, cancellationToken,
                    () => { foreach (var line in _lines.Values) line.RestoreCounters(new(), new()); });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Dernière sauvegarde des compteurs à l’arrêt impossible");
            }
        }
    }

    public override void Dispose()
    {
        if (_plc is IPlcReadback readback) readback.TagChanged -= RecordPlcTagChange;
        (_plc as IDisposable)?.Dispose();
        base.Dispose();
    }

    public IReadOnlyList<LineSnapshot> GetSnapshots() => _lines.Values.Select(line => line.Snapshot()).OrderBy(x => x.LineId).ToArray();
    public Task<string> RestartRslinxAsync() => RestartRslinxCoreAsync(false);
    public Task<string> RestartRslinxAutomaticallyAsync(CancellationToken token = default) => RestartRslinxCoreAsync(true, token);
    private async Task<string> RestartRslinxCoreAsync(bool automatic, CancellationToken token = default)
    {
        if (!await _motionGate.WaitAsync(0)) throw new InvalidOperationException("Une commande automate est déjà en cours.");
        try
        {
            if (automatic && (!_configuration.RslinxRestart.AutoRestart ||
                !_lines.OrderBy(pair => pair.Key).First().Value.Running ||
                (_plc.IsConnected && (_plc is not IPlcReadback state || state.ReadsHealthy))))
                return "Redémarrage automatique annulé : connexion rétablie ou surveillance désactivée.";
            var plcOptions = _configuration.GetConfiguredLines().First().Plc;
            if (_configuration.Simulation) throw new InvalidOperationException("Redémarrage RSLinx désactivé en simulation.");
            if (string.Equals(plcOptions.Protocol, "Tcp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Le protocole passerelle TCP ne permet pas de redémarrer RSLinx.");
            if (string.Equals(plcOptions.Protocol, "OpcDa", StringComparison.OrdinalIgnoreCase) &&
                !IsLocalHost(plcOptions.OpcHost))
                throw new InvalidOperationException("RSLinx est configuré sur un serveur OPC distant. Le redémarrer sur ce serveur.");
            // On a communication failure the last motion value may be stale.
            // The automatic recovery requested by the operator never sends a motion command.
            if (!automatic && ConveyorRunning == true) throw new InvalidOperationException("Arrêter le convoyeur avant de redémarrer RSLinx.");
            if (_configuration.RslinxRestart.ValidationError() is { } error) throw new InvalidOperationException(error);
            if (_rslinxRestarter is null) throw new InvalidOperationException("Service de redémarrage RSLinx indisponible.");
            var reconnect = _lines.OrderBy(pair => pair.Key).First().Value.Running;
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            _rslinxRestartInProgress = true;
            _logger.LogWarning("Redémarrage RSLinx demandé ; interruption temporaire des échanges automate");
            await _plc.DisconnectAsync();
            ConveyorRunning = null;
            Changed?.Invoke();
            var reconnected = false;
            try { await _rslinxRestarter.RestartAsync(_configuration.RslinxRestart, timeout.Token); }
            finally
            {
                if (reconnect && !token.IsCancellationRequested)
                {
                    try { await _plc.ConnectAsync(timeout.Token); reconnected = _plc.IsConnected; }
                    catch (Exception exception) { _logger.LogWarning(exception, "Reconnexion automate différée après redémarrage RSLinx ; surveiller le voyant"); }
                }
            }
            return reconnect
                ? reconnected ? "RSLinx redémarré ; connexion automate réouverte. Vérifier le voyant et les lectures."
                    : "RSLinx redémarré ; reconnexion automate en attente. Consulter les journaux et le voyant."
                : "RSLinx redémarré. Les connexions appareils restent désactivées ; utiliser CONNECTER pour les ouvrir.";
        }
        finally { _rslinxRestartInProgress = false; _motionGate.Release(); Changed?.Invoke(); }
    }

    private static bool IsLocalHost(string host) => string.IsNullOrWhiteSpace(host) ||
        new[] { ".", "localhost", "127.0.0.1", "::1", Environment.MachineName }.Contains(host.Trim(), StringComparer.OrdinalIgnoreCase);

    public Task StartLineAsync(int lineId) => WithConnectionGateAsync(() => Get(lineId).StartAsync());
    public Task RestartLineAsync(int lineId) => WithConnectionGateAsync(async () =>
    {
        var line = Get(lineId);
        await line.StopAsync();
        await line.StartAsync();
    });
    public Task StopLineAsync(int lineId) => WithConnectionGateAsync(() => Get(lineId).StopAsync());
    private async Task WithConnectionGateAsync(Func<Task> action)
    {
        EnsureCountersReady();
        if (!await _motionGate.WaitAsync(0)) throw new InvalidOperationException("Une commande automate ou un redémarrage RSLinx est déjà en cours.");
        try { await action(); }
        finally { _motionGate.Release(); }
    }
    private void EnsureCountersReady()
    {
        if (_coordinateStatistics && !_statistics!.Initialized)
            throw new InvalidOperationException("Restauration des compteurs en cours. Vérifier la connexion MySQL avant de connecter les appareils.");
    }
    public void ResetCounters(int lineId) => Get(lineId).ResetCounters();
    public void SetCode98Enabled(int lineId, bool enabled) => Get(lineId).SetCode98Enabled(enabled);
    public Task TriggerScaleFaultTestAsync(int lineId) => Get(lineId).TriggerScaleFaultTestAsync();
    public Task SimulateParcelAsync(int lineId, string cameraData, Dimension dimension, decimal weight) => Get(lineId).SimulateAsync(cameraData, dimension, weight);
    private LineController Get(int lineId) => _lines.TryGetValue(lineId, out var line) ? line : throw new KeyNotFoundException($"Ligne {lineId} inconnue.");
}
