using Conveyor.Web.Domain;
using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

public sealed class ConveyorSupervisor : BackgroundService, IConveyorSupervisor
{
    private readonly Dictionary<int, LineController> _lines;
    private readonly string _recirculationDrainTag;
    public bool? RecirculationDrainActive { get; private set; }
    public Task StartRecirculationDrainAsync() => WithConnectionGateAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(_recirculationDrainTag)) throw new InvalidOperationException("Tag de recirculation non configuré.");
        if (!_plc.IsConnected) throw new InvalidOperationException("Automate déconnecté.");
        if (RecirculationDrainActive == true) return;
        if (_plc is TcpPlcGateway) throw new InvalidOperationException("Le vidage surveillé exige une connexion OPC DA ou DDE.");
        await _plc.SendChuteAsync(_recirculationDrainTag, 1, 1, CancellationToken.None);
        _logger.LogInformation("Vidage recirculation demandé : {Tag}=1", _recirculationDrainTag);
        RecirculationDrainActive = true;
        Changed?.Invoke();
    });

    internal async Task PollRecirculationAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_recirculationDrainTag) || !_plc.IsConnected || RecirculationDrainActive == false) return;
        await _motionGate.WaitAsync(token);
        try
        {
            var value = await _plc.ReadTagAsync(_recirculationDrainTag, token);
            RecordPlcTagChange(_recirculationDrainTag, value ?? "");
        }
        finally { _motionGate.Release(); }
    }

    private async Task MonitorRecirculationAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_recirculationDrainTag)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try { await PollRecirculationAsync(token); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Lecture du tag de vidage recirculation impossible");
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _manualLineStates = new();
    public bool? GetManualLineState(int lineId) => _plc.IsConnected && _manualLineStates.TryGetValue(lineId, out var state) ? state : null;

    internal async Task PollManualLinesAsync(CancellationToken token)
    {
        foreach (var tag in _configuration.GetConfiguredLines().Select(line => line.Plc.StopManuelTag?.Trim())
            .Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await _motionGate.WaitAsync(token);
            try
            {
                var value = _plc.IsConnected ? await _plc.ReadTagAsync(tag!, token) : null;
                RecordPlcTagChange(tag!, value ?? "");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            {
                RecordPlcTagChange(tag!, "");
                _logger.LogWarning(exception, "Lecture STOP_MANUEL impossible pour {Tag}", tag);
            }
            finally { _motionGate.Release(); }
        }
    }

    private async Task MonitorManualLinesAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            do { await PollManualLinesAsync(token); }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
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
    public bool CountersReady => !_coordinateStatistics || _statistics!.Initialized;
    private readonly IRslinxRestarter? _rslinxRestarter;
    private readonly string _closeChute39Tag;
    private readonly string _motionTag;
    private readonly string _fullChutesTag;
    private readonly string _code42Tag;
    private readonly string _chute4FullTag;
    private readonly Dictionary<int, string> _fullChuteTags;
    private readonly HashSet<int> _activeFullChutes = new();
    private readonly Dictionary<int, bool?> _fullChuteStates = new();
    private readonly Dictionary<int, bool?> _fullChuteCountStates = new();
    private readonly Dictionary<int, long> _fullChuteTransitions = new();
    private readonly Dictionary<int, FullChuteDuration> _fullChuteDurations = new();
    public IReadOnlyDictionary<int, TimeSpan?> FullChuteDurations
    {
        get
        {
            lock (_stopAndGoGate)
            {
                var now = DateTimeOffset.UtcNow;
                return _fullChuteTags.Keys.ToDictionary(chute => chute,
                    chute => _fullChuteCountStates.GetValueOrDefault(chute).HasValue
                        ? (TimeSpan?)(_fullChuteDurations.GetValueOrDefault(chute)?.GetTotal(now) ?? TimeSpan.Zero) : null);
            }
        }
    }
    public IReadOnlyDictionary<int, long?> FullChuteTransitions
    {
        get
        {
            lock (_stopAndGoGate)
                return _fullChuteTags.Keys.Order().ToDictionary(chute => chute,
                    chute => _fullChuteCountStates.GetValueOrDefault(chute).HasValue
                        ? (long?)_fullChuteTransitions.GetValueOrDefault(chute) : null);
        }
    }
    public IReadOnlyList<int> FullChutesActive
    {
        get
        {
            lock (_stopAndGoGate)
                return _fullChuteStates.Where(pair => pair.Value == true).Select(pair => pair.Key).Order().ToArray();
        }
    }
    public IReadOnlyList<int> FullChuteAlarms
    {
        get
        {
            lock (_stopAndGoGate)
                return _activeFullChutes.Order().ToArray();
        }
    }
    private readonly string _stopAndGoTag;
    private readonly TimeSpan _stopAndGoDelay;
    private readonly object _stopAndGoGate = new();
    private CancellationTokenSource? _stopAndGoPending;
    private bool? _chute4FullState;
    private bool? _chute4CountState;
    private long _chute4FullTransitions;
    public long? Chute4FullTransitions
    {
        get { lock (_stopAndGoGate) return _chute4CountState.HasValue ? _chute4FullTransitions : null; }
    }
    private bool? _stopAndGoState;
    private bool _stopAndGoStopping;
    public int CurrentShiftId => _configuration.General!.ShiftId;
    public bool? ConveyorRunning { get; private set; }
    public int? ConveyorId => _configuration.General?.ConveyorId;
    public string? ConveyorStopCause { get; private set; }
    public int? FullChutesCount { get; private set; }
    public int? Code42Count { get; private set; }
    public bool Chute4AlarmActive { get; private set; }
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
                    ConveyorStopCause = start ? null : cause switch { 0 => "PAUSE", 1 => "JAM", 2 => "DOWN", _ => null };
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
        CounterStatisticsService? statistics = null, IRslinxRestarter? rslinxRestarter = null,
        IPlcGateway? plcGateway = null, AxisParcelEvents? axisEvents = null)
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
        var sharedTags = configuration.General ?? new GeneralOptions();
        _recirculationDrainTag = sharedTags.RecirculationDrainTag?.Trim() ?? "";
        _fullChutesTag = sharedTags.FullChutesTag.Trim();
        _code42Tag = sharedTags.Code42Tag.Trim();
        _chute4FullTag = sharedTags.GetFullChuteTag(4);
        _fullChuteTags = Enumerable.Range(1, 48).Where(chute => chute != 4)
            .Select(chute => (Chute: chute, Tag: sharedTags.GetFullChuteTag(chute).Trim()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Tag)).ToDictionary(item => item.Chute, item => item.Tag);
        if (!string.IsNullOrWhiteSpace(_chute4FullTag)) _fullChuteTags[4] = _chute4FullTag;
        foreach (var chute in _fullChuteTags.Keys) _fullChuteStates[chute] = null;
        _stopAndGoTag = _fullChuteTags.Count == 0 ? "" : sharedTags.StopAndGoTag?.Trim() ?? "";
        _stopAndGoDelay = TimeSpan.FromSeconds(Math.Clamp(sharedTags.StopAndGoDelaySeconds, 1, 3_600));
        PlcConfiguration.Validate(primaryLine.Plc);
        var monitoredTags = activeLines.SelectMany(line => new[] { line.Plc.ChuteTag, line.Plc.TransferTag, line.Plc.ScaleFaultTag, line.Plc.StopManuelTag?.Trim() ?? "" })
            .Append(_closeChute39Tag).Append(_motionTag)
            .Append(_fullChutesTag).Append(_code42Tag)
            .Concat(_fullChuteTags.Values).Append(_chute4FullTag).Append(_stopAndGoTag).Append(_recirculationDrainTag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray();
        var subscriptionOnlyTags = activeLines.SelectMany(line => new[] { line.Plc.ChuteTag, line.Plc.TransferTag, line.Plc.StopManuelTag?.Trim() ?? "" })
            .Concat(_fullChuteTags.Values).Append(_chute4FullTag).Append(_stopAndGoTag).Append(_recirculationDrainTag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray();
        _plc = plcGateway ?? (configuration.Simulation
            ? new SimulationPlcGateway(loggerFactory.CreateLogger<SimulationPlcGateway>())
            : string.Equals(primaryLine.Plc.Protocol, "Tcp", StringComparison.OrdinalIgnoreCase)
                ? new TcpPlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<TcpPlcGateway>())
                : string.Equals(primaryLine.Plc.Protocol, "OpcDa", StringComparison.OrdinalIgnoreCase)
                    ? new OpcDaPlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<OpcDaPlcGateway>(), monitoredTags, subscriptionOnlyTags)
                    : new DdePlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<DdePlcGateway>(), monitoredTags));
        var logger = loggerFactory.CreateLogger<ConveyorSupervisor>();
        foreach (var line in activeLines.Where(line => !line.Enabled))
            logger.LogInformation("Ligne {Line} : démarrage automatique désactivé; appareils non connectés jusqu’au START", line.Id + 1);
        _autoStartIds = activeLines.Where(line => line.Enabled).Select(line => line.Id).ToHashSet();
        _lines = activeLines.ToDictionary(line => line.Id, line =>
        {
            var controller = new LineController(line, configuration.Simulation, repository, _plc,
                line.Id == primaryLine.Id, sortEngine,
                loggerFactory.CreateLogger($"Conveyor.Line.{line.Id}"), () => Changed?.Invoke(), sms,
                automaticCounterReset: !_coordinateStatistics,
                axisEvents: configuration.AxisCamera.Enabled ? axisEvents : null);
            controller.SetMaintenance(Maintenance);
            return controller;
        });
        if (_plc is IPlcReadback readback) readback.TagChanged += RecordPlcTagChange;
    }

    internal void RecordPlcTagChange(string tag, string value)
    {
        var binaryState = value.Trim('\0', ' ', '\r', '\n', '\t') switch { "1" => true, "0" => false, _ => (bool?)null };
        if (!string.IsNullOrWhiteSpace(_recirculationDrainTag) && string.Equals(tag, _recirculationDrainTag, StringComparison.OrdinalIgnoreCase))
        {
            RecirculationDrainActive = binaryState;
            Changed?.Invoke();
        }
        foreach (var line in _configuration.GetConfiguredLines().Where(line =>
            !string.IsNullOrWhiteSpace(line.Plc.StopManuelTag) &&
            string.Equals(line.Plc.StopManuelTag.Trim(), tag, StringComparison.OrdinalIgnoreCase)))
        {
            if (binaryState.HasValue) _manualLineStates[line.Id] = binaryState.Value;
            else _manualLineStates.TryRemove(line.Id, out _);
            Changed?.Invoke();
        }
        lock (_stopAndGoGate)
        {
            var matched = _fullChuteTags.Where(pair => string.Equals(pair.Value, tag, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray();
            foreach (var chute in matched)
            {
                if (!_fullChuteDurations.TryGetValue(chute, out var duration))
                    _fullChuteDurations[chute] = duration = new();
                duration.Observe(binaryState, DateTimeOffset.UtcNow);
                if (_fullChuteCountStates.GetValueOrDefault(chute) == false && binaryState == true)
                    _fullChuteTransitions[chute] = _fullChuteTransitions.GetValueOrDefault(chute) + 1;
                _fullChuteCountStates[chute] = binaryState;
                _fullChuteStates[chute] = binaryState;
                if (binaryState == true) _activeFullChutes.Add(chute);
            }
            if (matched.Length > 0)
            {
                if (_fullChuteStates.Values.Any(state => state == true)) ScheduleStopAndGo(true);
                else if (_fullChuteStates.Values.All(state => state == false)) ScheduleStopAndGo(false);
                else
                {
                    _chute4FullState = null;
                    _stopAndGoPending?.Cancel();
                    _stopAndGoPending = null;
                }
                UpdateChute4AlarmLocked();
                Changed?.Invoke();
            }
        }
        if (!string.IsNullOrWhiteSpace(_chute4FullTag) &&
            string.Equals(tag, _chute4FullTag, StringComparison.OrdinalIgnoreCase))
        {
            lock (_stopAndGoGate)
            {
                // The first reading establishes the baseline; only observed 0 -> 1 edges count.
                if (_chute4CountState == false && binaryState == true) _chute4FullTransitions++;
                _chute4CountState = binaryState;
            }
            Changed?.Invoke();
        }
        if (!string.IsNullOrWhiteSpace(_stopAndGoTag) &&
            string.Equals(tag, _stopAndGoTag, StringComparison.OrdinalIgnoreCase) && binaryState.HasValue)
            RecordStopAndGoState(binaryState.Value);

        var fullChutes = !string.IsNullOrWhiteSpace(_fullChutesTag) &&
            string.Equals(tag, _fullChutesTag, StringComparison.OrdinalIgnoreCase);
        var code42 = !string.IsNullOrWhiteSpace(_code42Tag) &&
            string.Equals(tag, _code42Tag, StringComparison.OrdinalIgnoreCase);
        if (fullChutes || code42)
        {
            int? count = int.TryParse(value.Trim('\0', ' ', '\r', '\n', '\t'), out var parsed) && parsed >= 0 ? parsed : null;
            if (fullChutes) FullChutesCount = count;
            if (code42) Code42Count = count;
            Changed?.Invoke();
        }
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
                if (state != false) ConveyorStopCause = null;
                ConveyorRunning = state;
                foreach (var line in _lines.Values) line.SetConveyorRunning(state);
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

    private void RecordStopAndGoState(bool state)
    {
        bool? desired = null;
        bool alarmChanged;
        lock (_stopAndGoGate)
        {
            _stopAndGoState = state;
            alarmChanged = UpdateChute4AlarmLocked();
            if (_chute4FullState == state)
            {
                _stopAndGoPending?.Cancel();
                desired = null;
            }
            else if (_stopAndGoPending is null) desired = _chute4FullState;
        }
        if (alarmChanged) Changed?.Invoke();
        if (desired.HasValue) ScheduleStopAndGo(desired.Value);
    }

    private void ScheduleStopAndGo(bool state)
    {
        CancellationTokenSource? pending = null;
        bool alarmChanged;
        lock (_stopAndGoGate)
        {
            if (_stopAndGoStopping) return;
            var sameInput = _chute4FullState == state;
            _chute4FullState = state;
            alarmChanged = UpdateChute4AlarmLocked();
            if (!string.IsNullOrWhiteSpace(_stopAndGoTag) && !(sameInput && _stopAndGoPending is not null))
            {
                _stopAndGoPending?.Cancel();
                if (_stopAndGoState == state)
                    _stopAndGoPending = null;
                else
                {
                    pending = new CancellationTokenSource();
                    _stopAndGoPending = pending;
                }
            }
        }
        if (alarmChanged) Changed?.Invoke();
        if (pending is not null) _ = ApplyStopAndGoAfterDelayAsync(state, pending);
    }

    private bool UpdateChute4AlarmLocked()
    {
        var removed = 0;
        if (string.IsNullOrWhiteSpace(_stopAndGoTag) || _stopAndGoState == false)
            removed = _activeFullChutes.RemoveWhere(chute => _fullChuteStates[chute] == false);
        // L'alarme s'ouvre immédiatement sur le signal d'entrée. Une fois ouverte,
        // elle reste verrouillée jusqu'au retour OPC confirmé des deux tags à zéro.
        var active = _activeFullChutes.Contains(4);
        if (active == Chute4AlarmActive) return removed > 0;
        Chute4AlarmActive = active;
        return true;
    }

    private async Task ApplyStopAndGoAfterDelayAsync(bool state, CancellationTokenSource pending)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_stopAndGoTag)) return;
            await Task.Delay(_stopAndGoDelay, pending.Token);
            lock (_stopAndGoGate)
            {
                if (!ReferenceEquals(_stopAndGoPending, pending) || _chute4FullState != state || _stopAndGoState == state)
                    return;
            }
            await _plc.SendChuteAsync(_stopAndGoTag, state ? 1 : 0, 1, pending.Token);
            _logger.LogWarning("Automatisme chutes pleines : état commun stable à {InputState} pendant {DelaySeconds} s ; {OutputTag}={OutputState}",
                state ? 1 : 0, _stopAndGoDelay.TotalSeconds, _stopAndGoTag, state ? 1 : 0);
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Automatisme chute 4 : impossible d'écrire {State} dans {OutputTag}", state ? 1 : 0, _stopAndGoTag);
        }
        finally
        {
            lock (_stopAndGoGate)
                if (ReferenceEquals(_stopAndGoPending, pending)) _stopAndGoPending = null;
            pending.Dispose();
        }
    }

    private void StopStopAndGoAutomation()
    {
        lock (_stopAndGoGate)
        {
            _stopAndGoStopping = true;
            _stopAndGoPending?.Cancel();
            _stopAndGoPending = null;
        }
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
        var recirculationMonitor = MonitorRecirculationAsync(stoppingToken);
        var manualLineMonitor = MonitorManualLinesAsync(stoppingToken);
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
        finally { await Task.WhenAll(recirculationMonitor, manualLineMonitor); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        StopStopAndGoAutomation();
        try
        {
            await Task.WhenAll(_lines.Values.Select(line => line.StopAsync()));
        }
        finally
        {
            // Close the shared transport even if the primary line was already stopped
            // or a device failed during shutdown. Do not defer COM cleanup to DI disposal.
            try { await _plc.DisconnectAsync(); }
            finally { await base.StopAsync(cancellationToken); }
        }
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
        StopStopAndGoAutomation();
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
            foreach (var line in _lines.Values) line.SetConveyorRunning(null);
            ConveyorStopCause = null;
            FullChutesCount = null;
            lock (_stopAndGoGate)
            {
                _chute4CountState = null;
                _fullChuteCountStates.Clear();
                foreach (var duration in _fullChuteDurations.Values)
                    duration.Observe(null, DateTimeOffset.UtcNow);
            }
            Code42Count = null;
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
    public event Action? CountersReset;
    public void ResetCounters(int lineId)
    {
        Get(lineId).ResetCounters();
        lock (_stopAndGoGate)
        {
            _fullChuteTransitions.Clear();
            _chute4FullTransitions = 0;
            _fullChuteDurations.Clear();
            var now = DateTimeOffset.UtcNow;
            foreach (var chute in _fullChuteTags.Keys)
            {
                var duration = new FullChuteDuration();
                duration.Observe(_fullChuteCountStates.GetValueOrDefault(chute), now);
                _fullChuteDurations[chute] = duration;
            }
        }
        CountersReset?.Invoke();
        Changed?.Invoke();
    }
    public void SetCode98Enabled(int lineId, bool enabled) => Get(lineId).SetCode98Enabled(enabled);
    public Task SetLineMotionAsync(int lineId, bool start) => WithConnectionGateAsync(() => Get(lineId).SetLineMotionAsync(start));
    public Task SetManualLineAsync(int lineId, bool start) => WithConnectionGateAsync(async () =>
    {
        var line = _configuration.GetConfiguredLines().Single(line => line.Id == lineId);
        var tag = line.Plc.StopManuelTag?.Trim();
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidOperationException("Tag STOP_MANUEL non configuré pour cette ligne.");
        if (!_plc.IsConnected) throw new InvalidOperationException("Automate déconnecté.");
        await _plc.SendChuteAsync(tag, start ? 0 : 1, 1, CancellationToken.None);
        _logger.LogInformation("Ligne {Line}: commande {Command} — {Tag}={Value}", lineId + 1,
            start ? "DÉPART LIGNE" : "ARRÊT LIGNE", tag, start ? 0 : 1);
        Changed?.Invoke();
    });
    public Task TriggerScaleFaultTestAsync(int lineId) => Get(lineId).TriggerScaleFaultTestAsync();
    public Task SimulateParcelAsync(int lineId, string cameraData, Dimension dimension, decimal weight) => Get(lineId).SimulateAsync(cameraData, dimension, weight);
    private LineController Get(int lineId) => _lines.TryGetValue(lineId, out var line) ? line : throw new KeyNotFoundException($"Ligne {lineId} inconnue.");
}
