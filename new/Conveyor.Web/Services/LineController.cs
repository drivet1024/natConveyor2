using System.Threading.Channels;
using System.Net.Sockets;
using Conveyor.Web.Domain;
using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;

namespace Conveyor.Web.Services;

internal sealed class LineController
{
    private readonly object _gate = new();
    private readonly LineOptions _options;
    private readonly bool _simulation;
    private readonly IConveyorRepository _repository;
    private readonly IPlcGateway _plc;
    private readonly bool _controlsPlcConnection;
    private readonly SortEngine _sortEngine;
    private readonly ILogger _logger;
    private readonly Action _changed;
    private readonly ISmsAlerts? _sms;
    private readonly bool _automaticCounterReset;
    private CancellationTokenSource? _stopping;
    private List<Task> _tasks = [];
    private TcpFrameReceiver? _cameraReceiver;
    private TcpFrameClient? _cameraClient;
    private TcpFrameReceiver? _dimensionReceiver;
    private TcpFrameClient? _dimensionClient;
    private TcpFrameReceiver? _scaleReceiver;
    private TcpFrameClient? _scaleClient;
    private TimedValue<Dimension>? _lastDimension;
    private TimedValue<decimal>? _lastWeight;
    private long _lastUsedDimensionSequence;
    private long _lastUsedScaleSequence;
    private SortDecision? _lastDecision;
    private DeviceReception? _plcInput;
    private DeviceReception? _plcTransferInput;
    private long _parcelSequenceSinceReset;
    private bool _chute39Closed;
    private bool? _scaleFaultActive;
    public void RecordScaleFaultReception(string value)
    {
        var active = value.Trim('\0', ' ', '\r', '\n', '\t') switch { "1" => true, "0" => false, _ => (bool?)null };
        lock (_gate) _scaleFaultActive = active;
        _changed();
    }
    public void SetChute39Closed(bool closed)
    {
        lock (_gate)
        {
            if (_chute39Closed == closed) return;
            _chute39Closed = closed;
        }
        _logger.LogInformation("Ligne {Line} : chute 39 {State}", _options.Id + 1,
            closed ? "fermée — recirculation vers 97" : "ouverte");
    }
    private int ResolveClosedChute(int chute)
    {
        lock (_gate) return _chute39Closed && chute == 39 ? 97 : chute;
    }
    public void RecordPlcReception(string value)
    {
        if (string.Equals(value.Trim('\0', ' ', '\r', '\n', '\t'), "68", StringComparison.Ordinal)) return;
        lock (_gate)
        {
            _plcInput = new(value, DateTimeOffset.Now, (_plcInput?.Sequence ?? 0) + 1);
        }
        _changed();
    }
    private DeviceReception? _cameraInput;
    private DateTimeOffset? _lastParcelReceivedAt;
    private DeviceReception? _dimensionInput;
    private DeviceReception? _scaleInput;
    private PlcDispatch? _lastPlcDispatch;
    private ParcelContext? _lastDispatchedParcel;
    private readonly Queue<PlcDispatch> _recentPlcDispatches = new();
    private readonly ChuteTrafficWindow _chuteTraffic = new();
    private long _dispatchSequence;
    private int _consecutiveParcelsWithoutScale;
    private Channel<bool> _scaleFaultRequests = Channel.CreateUnbounded<bool>();
    private readonly SemaphoreSlim _scaleFaultPulseGate = new(1, 1);

    private void RecordReception(string device, string frame)
    {
        DeviceReception input;
        lock (_gate)
        {
            var previous = device switch { "camera" => _cameraInput, "dimension" => _dimensionInput, _ => _scaleInput };
            input = new DeviceReception(frame.Length > 4096 ? frame[..4096] : frame,
                DateTimeOffset.Now, (previous?.Sequence ?? 0) + 1, frame.Length > 4096);
            switch (device)
            {
                case "camera": _cameraInput = input; _lastParcelReceivedAt = input.ReceivedAt; break;
                case "dimension": _dimensionInput = input; break;
                default: _scaleInput = input; break;
            }
        }
        if (device == "camera")
            _logger.LogInformation("TRACE CAMÉRA — ligne {Line}: caméra=[{CameraData}] reçue à {CameraAt}, séquence={CameraSequence}",
                _options.Id + 1, FormatFrame(frame), FormatTimestamp(input.ReceivedAt), input.Sequence);
        _changed();
    }
    private LineCounters _productionCounters = new();
    private LineCounters _maintenanceCounters = new();
    private bool _maintenance;
    private bool _conveyorRunning;
    private DateTimeOffset? _sortingTick;
    private void AccumulateSortingTime(DateTimeOffset now)
    {
        if (_conveyorRunning && _sortingTick is { } previous)
            _counters.SortingRunSeconds += Math.Max(0, (now - previous).TotalSeconds);
        _sortingTick = _conveyorRunning ? now : null;
    }
    internal void SetConveyorRunning(bool? running, DateTimeOffset? timestamp = null)
    {
        lock (_gate)
        {
            var now = timestamp ?? DateTimeOffset.UtcNow;
            AccumulateSortingTime(now);
            _conveyorRunning = running == true;
            _sortingTick = _conveyorRunning ? now : null;
        }
    }
    private LineCounters _counters
    {
        get => _maintenance ? _maintenanceCounters : _productionCounters;
        set { if (_maintenance) _maintenanceCounters = value; else _productionCounters = value; }
    }

    public void SetMaintenance(bool maintenance)
    {
        lock (_gate)
        {
            AccumulateSortingTime(DateTimeOffset.UtcNow);
            _maintenance = maintenance;
            _lastDecision = null;
            _consecutiveParcelsWithoutScale = 0;
        }
    }
    private string? _lastError;
    private bool _databaseConnected;
    private bool _plcConnected;
    private DateOnly _lastReset = DateOnly.FromDateTime(DateTime.Now);

    public LineController(LineOptions options, bool simulation, IConveyorRepository repository, IPlcGateway plc,
        bool controlsPlcConnection,
        SortEngine sortEngine, ILogger logger, Action changed, ISmsAlerts? sms = null, bool automaticCounterReset = true)
    {
        _options = options;
        _simulation = simulation;
        _repository = repository;
        _plc = plc;
        _controlsPlcConnection = controlsPlcConnection;
        _sortEngine = sortEngine;
        _logger = logger;
        _changed = changed;
        _sms = sms;
        _automaticCounterReset = automaticCounterReset;
        // With scheduled capture enabled, a startup before today's reset must still
        // reset today, otherwise tomorrow's snapshot would contain two shifts.
        var now = DateTime.Now;
        if (!automaticCounterReset && TimeOnly.FromDateTime(now) < options.EndOfDay)
            _lastReset = DateOnly.FromDateTime(now).AddDays(-1);
    }

    public bool Running => _stopping is { IsCancellationRequested: false };

    public async Task StartAsync()
    {
        if (Running) return;
        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
        _scaleFaultRequests = Channel.CreateUnbounded<bool>();
        _tasks = [ConsumeScaleFaultRequestsAsync(_scaleFaultRequests.Reader, token)];
        _logger.LogInformation("Ligne {Line} : démarrage en mode {Mode}; vérification MySQL avant les connexions appareils", _options.Id + 1, _simulation ? "simulation" : "production");
        _databaseConnected = await _repository.PingAsync(token);
        if (_simulation)
            _logger.LogInformation("Ligne {Line} : mode simulation actif, les connexions aux appareils physiques sont désactivées", _options.Id + 1);
        if (_controlsPlcConnection)
        {
            try
            {
                await _plc.ConnectAsync(token);
                _lastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _lastError = $"Automate: {exception.Message}";
                _logger.LogError(exception, "Impossible de démarrer la connexion automate pour la ligne principale");
            }
        }
        _plcConnected = _plc.IsConnected;
        if (!_simulation)
        {
            var camera = Channel.CreateBounded<string>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            var dimension = Channel.CreateBounded<string>(1_000);
            var scale = Channel.CreateBounded<string>(1_000);
            _tasks.AddRange(
            [
                ConsumeCameraAsync(camera.Reader, token),
                ConsumeDimensionsAsync(dimension.Reader, token),
                ConsumeScaleAsync(scale.Reader, token),
                MonitorAsync(token)
            ]);
            if (_options.CameraConnectMode)
            {
                _cameraClient = new(_options.CameraHost, _options.CameraPort, "\r", _logger, _changed, "Caméra", frame => RecordReception("camera", frame));
                _tasks.Add(_cameraClient.RunAsync(camera.Writer, token));
            }
            else
            {
                _cameraReceiver = new(_options.CameraPort, "\r", _logger, _changed, "Caméra", frame => RecordReception("camera", frame));
                _tasks.Add(_cameraReceiver.RunAsync(camera.Writer, token));
            }
            if (_options.ScaleConnectMode)
            {
                _scaleClient = new(_options.ScaleHost, _options.ScalePort, "\r\n", _logger, _changed, "Balance", frame => RecordReception("scale", frame));
                _tasks.Add(_scaleClient.RunAsync(scale.Writer, token));
            }
            else
            {
                _scaleReceiver = new(_options.ScalePort, "\r\n", _logger, _changed, "Balance", frame => RecordReception("scale", frame));
                _tasks.Add(_scaleReceiver.RunAsync(scale.Writer, token));
            }
            if (_options.DimensionConnectMode)
            {
                _dimensionClient = new(_options.DimensionHost, _options.DimensionPort, "\u0003", _logger, _changed, "Dimensionneur", RecordDimensionReception);
                _tasks.Add(_dimensionClient.RunAsync(dimension.Writer, token));
            }
            else
            {
                _dimensionReceiver = new(_options.DimensionPort, "\u0003", _logger, _changed, "Dimensionneur", RecordDimensionReception);
                _tasks.Add(_dimensionReceiver.RunAsync(dimension.Writer, token));
            }
        }
        _logger.LogInformation("Ligne {Line} démarrée", _options.Id);
        _sms?.Notify("Connexion des appareils activée (vérifier les voyants)", _options.Id);
        _changed();
    }

    public async Task StopAsync()
    {
        if (_stopping is null) return;
        _stopping.Cancel();
        _cameraReceiver?.Stop();
        _dimensionReceiver?.Stop();
        _scaleReceiver?.Stop();
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException or SocketException) { }
        _tasks = [];
        if (_controlsPlcConnection) await _plc.DisconnectAsync();
        _plcConnected = _plc.IsConnected;
        _stopping.Dispose();
        _stopping = null;
        lock (_gate)
        {
            _consecutiveParcelsWithoutScale = 0;
        }
        _logger.LogInformation("Ligne {Line} arrêtée", _options.Id);
        _sms?.Notify("Déconnexion des appareils effectuée", _options.Id);
        _changed();
    }

    public void RestoreCounters(LineCounters production, LineCounters maintenance)
    {
        lock (_gate)
        {
            _productionCounters = production.Copy();
            _maintenanceCounters = maintenance.Copy();
            _sortingTick = _conveyorRunning ? DateTimeOffset.UtcNow : null;
            _parcelSequenceSinceReset = Math.Max(0, production.TotalParcels) + Math.Max(0, maintenance.TotalParcels);
            _consecutiveParcelsWithoutScale = 0;
        }
        _changed();
    }

    public void ResetCounters()
    {
        lock (_gate)
        {
            _counters = new LineCounters();
            _sortingTick = _conveyorRunning ? DateTimeOffset.UtcNow : null;
            _lastDecision = null;
            _plcInput = null;
            _cameraInput = null;
            _dimensionInput = null;
            _scaleInput = null;
            _lastPlcDispatch = null;
            _lastDispatchedParcel = null;
            _recentPlcDispatches.Clear();
            _lastUsedDimensionSequence = 0;
            _lastUsedScaleSequence = 0;
            _parcelSequenceSinceReset = 0;
            _consecutiveParcelsWithoutScale = 0;
        }
        _sms?.Notify("Reset compteurs effectué", _options.Id);
        _changed();
    }
    public void RecordPlcTransferReception(string value)
    {
        DeviceReception reception;
        PlcDispatch? dispatch;
        ParcelContext? parcel;
        lock (_gate)
        {
            reception = new(value, DateTimeOffset.Now, (_plcTransferInput?.Sequence ?? 0) + 1);
            _plcTransferInput = reception;
            dispatch = _lastPlcDispatch;
            parcel = _lastDispatchedParcel;
        }
        if (IsCode68(value))
            _logger.LogInformation(
                "TRACE TRANSFERT — dimensions={Dimensions}; ligne {Line}, dernier colis envoyé #{ParcelId}; " +
                "caméra=[{CameraData}] reçue à {CameraAt}; caméra → envoi automate={CameraToSendMs} ms; " +
                "envoi automate → transfert={SendToTransferMs} ms; {TransferTag}=68 reçu à {TransferAt}; association indicative",
                parcel is null ? "absentes" : FormatDimensions(parcel.Dimension, parcel.DimensionTimestamp),
                _options.Id + 1, parcel?.ParcelId,
                parcel is null ? "absente" : FormatFrame(parcel.CameraData), FormatTime(parcel?.CameraTimestamp),
                dispatch is null || parcel is null ? (long?)null : (long)(dispatch.SentAt - parcel.CameraTimestamp).TotalMilliseconds,
                dispatch is null ? (long?)null : (long)(reception.ReceivedAt - dispatch.SentAt).TotalMilliseconds,
                _options.Plc.TransferTag, FormatTime(reception.ReceivedAt));
        _changed();
    }

    private void RecordDimensionReception(string frame)
    {
        if (!SensorParsers.IsDimensionControlFrame(frame))
            RecordReception("dimension", frame);
    }

    public void SetCode98Enabled(bool enabled)
    {
        _options.ValidateDimensionsAndWeight = enabled;
        _logger.LogInformation("Ligne {Line}: code 98 {State}", _options.Id, enabled ? "activé" : "désactivé");
        _changed();
    }

    public Task TriggerScaleFaultTestAsync() => SendScaleFaultPulseAsync(CancellationToken.None);

    public async Task SetLineMotionAsync(bool start)
    {
        if (string.IsNullOrWhiteSpace(_options.Plc.ScaleFaultTag))
            throw new InvalidOperationException($"Aucun tag de faute balance n'est configuré pour la ligne {_options.Id + 1}.");
        if (!_plc.IsConnected) throw new InvalidOperationException("La connexion automate n’est pas disponible.");
        if (!await _scaleFaultPulseGate.WaitAsync(0))
            throw new InvalidOperationException("Une impulsion faute balance est en cours sur cette ligne. Réessayer après sa fin.");
        try
        {
            var value = start ? 1 : 0;
            await _plc.SendChuteAsync(_options.Plc.ScaleFaultTag, value, 1, CancellationToken.None);
            _logger.LogInformation("Ligne {Line} : commande {Action}, valeur {Value} envoyée au tag {Tag}",
                _options.Id + 1, start ? "DÉMARRER LIGNE" : "ARRÊTER LIGNE", value, _options.Plc.ScaleFaultTag);
            // Reflect the accepted write; subsequent PLC notifications remain authoritative.
            RecordScaleFaultReception(value.ToString());
        }
        finally { _scaleFaultPulseGate.Release(); }
    }

    public async Task SimulateAsync(string cameraData, Dimension dimension, decimal weight)
    {
        if (!_simulation) throw new InvalidOperationException("L'injection est disponible uniquement en mode simulation.");
        if (!Running) await StartAsync();
        var now = DateTimeOffset.Now;
        lock (_gate)
        {
            _lastDimension = new(dimension, now);
            _lastWeight = new(weight, now);
            _counters.DimensionReads++;
            _counters.ScaleReads++;
        }
        RecordReception("camera", cameraData);
        RecordReception("dimension", $"{dimension.Length} × {dimension.Width} × {dimension.Height}");
        RecordReception("scale", weight.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await HandleCameraAsync(cameraData, now, _stopping!.Token);
    }

    private async Task ConsumeCameraAsync(ChannelReader<string> reader, CancellationToken token)
    {
        await foreach (var frame in reader.ReadAllAsync(token)) await HandleCameraAsync(frame, DateTimeOffset.Now, token);
    }

    private async Task ConsumeDimensionsAsync(ChannelReader<string> reader, CancellationToken token)
    {
        await foreach (var frame in reader.ReadAllAsync(token))
        {
            var value = SensorParsers.ParseDimension(frame);
            if (value is null) continue;
            lock (_gate) { _lastDimension = new(value, DateTimeOffset.Now); _counters.DimensionReads++; }
            _changed();
        }
    }

    private async Task ConsumeScaleAsync(ChannelReader<string> reader, CancellationToken token)
    {
        await foreach (var frame in reader.ReadAllAsync(token))
        {
            var value = SensorParsers.ParseWeight(frame, _options.ScaleProtocol);
            if (value is null) continue;
            lock (_gate) { _lastWeight = new(value.Value, DateTimeOffset.Now); _counters.ScaleReads++; }
            _changed();
        }
    }

    private async Task HandleCameraAsync(string frame, DateTimeOffset timestamp, CancellationToken token)
    {
        LineCounters parcelCounters;
        bool parcelMaintenance;
        long parcelId;
        long? code68Count = null;
        DateTimeOffset? code68CountedAt = null;
        DeviceReception? code68Transfer = null;
        DeviceReception? plcAtCode68 = null;
        PlcDispatch? previousDispatchAtCode68 = null;
        lock (_gate)
        {
            parcelCounters = _counters;
            parcelMaintenance = _maintenance;
            parcelId = ++_parcelSequenceSinceReset;
            parcelCounters.CameraReads++;
            parcelCounters.TotalParcels++;
            if (IsCode68(_plcTransferInput?.Raw))
            {
                code68Count = ++parcelCounters.Code68;
                code68CountedAt = DateTimeOffset.Now;
                code68Transfer = _plcTransferInput;
                plcAtCode68 = _plcInput;
                previousDispatchAtCode68 = _lastPlcDispatch;
            }
        }
        if (_options.CorrelationDelayMs > 0) await Task.Delay(_options.CorrelationDelayMs, token);
        // Wait first so measurements received shortly after the camera frame are
        // included in the same parcel decision.
        var window = TimeSpan.FromMilliseconds(_options.CorrelationWindowMs);
        var (dimension, weight) = CaptureMeasurements(timestamp, window);
        var hasCorrelatedDimension = dimension is not null && (timestamp - dimension.Timestamp).Duration() <= window;
        var hasCorrelatedWeight = weight is not null && (timestamp - weight.Timestamp).Duration() <= window;
        var parcel = new ParcelContext(frame, timestamp,
            hasCorrelatedDimension ? dimension!.Value : Dimension.Missing,
            dimension?.Timestamp,
            hasCorrelatedWeight ? NormalizeWeight(weight!.Value, _options.UnderweightReplacementWeight) : -1,
            weight?.Timestamp,
            parcelId);

        var stage = "calcul de la chute";
        var recirculationCounted = false;
        var code98Sent = false;
        int? finalDispatchedChute = null;
        DateTimeOffset? finalDispatchedAt = null;
        SortDecision? classifiedDecision = null;
        var successfullySortedByPostalCode = false;
        try
        {
            var decision = await _sortEngine.DecideAsync(_options, parcel, token);
            // Recheck both sensors immediately before the PLC command. A newer
            // measurement may have arrived while the route was being resolved,
            // even when an older in-window measurement was already captured.
            var (lateDimension, lateWeight) = CaptureMeasurements(timestamp, window);
            var lateDimensionCorrelated = lateDimension is not null && (timestamp - lateDimension.Timestamp).Duration() <= window;
            var lateWeightCorrelated = lateWeight is not null && (timestamp - lateWeight.Timestamp).Duration() <= window;
            if (lateDimensionCorrelated || lateWeightCorrelated)
            {
                stage = "actualisation des mesures avant l'envoi automate";
                hasCorrelatedDimension |= lateDimensionCorrelated;
                hasCorrelatedWeight |= lateWeightCorrelated;
                parcel = parcel with
                {
                    Dimension = lateDimensionCorrelated ? lateDimension!.Value : parcel.Dimension,
                    DimensionTimestamp = lateDimensionCorrelated ? lateDimension!.Timestamp : parcel.DimensionTimestamp,
                    Weight = lateWeightCorrelated
                        ? NormalizeWeight(lateWeight!.Value, _options.UnderweightReplacementWeight)
                        : parcel.Weight,
                    WeightTimestamp = lateWeightCorrelated ? lateWeight!.Timestamp : parcel.WeightTimestamp
                };
                decision = await _sortEngine.DecideAsync(_options, parcel, token);
            }
            classifiedDecision = decision;
            var postalRoute = decision.Reason == "Route du code postal" && decision.PlcChute != _options.RejectedChute;
            var isNoRead = decision.CountNoRead && !postalRoute;
            RecordScalePresenceForParcel(hasCorrelatedWeight, parcelCounters);
            lock (_gate)
            {
                parcelCounters.RecordMeasurements(parcel.Dimension, parcel.Weight, _options.MaximumDimension, _options.MaximumWeight);
                if (IsSmallParcel(parcel.Dimension, _options.SmallParcelMaximumSide) &&
                    IsLightParcel(parcel.Weight, _options.LightParcelMaximumWeight))
                {
                    parcelCounters.SmallParcels++;
                    parcelCounters.RecordSmallParcel(parcel);
                }
                if (IsLightParcel(parcel.Weight, _options.LightParcelMaximumWeight)) parcelCounters.LightParcels++;
                if (IsInverseLengthParcel(parcel.Dimension)) parcelCounters.InverseLengthParcels++;
            }
            var routingReason = decision.Reason;
            var effectiveChute = ResolveClosedChute(decision.PlcChute);
            if (effectiveChute != decision.PlcChute)
            {
                decision = decision with { Chute = effectiveChute, PlcChute = effectiveChute,
                    Reason = $"Recirculation code 97 — chute 39 fermée ({routingReason})" };
                _logger.LogInformation("Ligne {Line} : colis {Barcode}, chute 39 remplacée par 97", _options.Id + 1, decision.Barcode);
            }
            _databaseConnected = true;
            stage = "envoi de la chute à l’automate (insertion non effectuée)";
            finalDispatchedAt = await SendParcelToPlcAsync(decision.PlcChute, _options.Plc.SendCount, parcel, fallback: false, token);
            finalDispatchedChute = decision.PlcChute;
            _plcConnected = true;
            code98Sent = decision.PlcChute == 98;
            if (code98Sent && !string.IsNullOrWhiteSpace(decision.Barcode))
            {
                try
                {
                    var passCount = await _repository.RecordExceptionPassAsync("98", decision.Barcode, token);
                    decision = decision with { Code98PassCount = passCount };
                    if (passCount == 3)
                        lock (_gate) parcelCounters.Code98RecirculatedOverTwice++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A statistics failure must never redirect a code 98 parcel to the reject chute.
                    _logger.LogWarning(exception, "Impossible de compter le passage code 98 du colis {Barcode}", decision.Barcode);
                }
            }
            if (decision.PlcChute == 97)
            {
                lock (_gate) parcelCounters.Code97++;
                recirculationCounted = true;
            }
            if (!isNoRead && decision.PlcChute == _options.RejectedChute)
            {
                lock (_gate)
                {
                    parcelCounters.CountRejection(routingReason);
                    if (routingReason == "Route de l'expédition non configurée") parcelCounters.RecordUnconfiguredRoute(decision);
                    if (routingReason == "Plusieurs expéditions détectées") parcelCounters.RecordMultipleBarcodes(parcel);
                }
            }
            stage = parcelMaintenance
                ? "insertion MySQL du scan en maintenance"
                : "insertion MySQL du scan";
            await _repository.SaveScanAsync(_options.Id, _options.DatabaseLineId, parcel, decision, token);
            lock (_gate)
            {
                _lastDecision = decision;
                _lastError = null;
                parcelCounters.DatabaseInserts++;
                if (decision.Chute == 98) parcelCounters.Code98++;
                if (!isNoRead && routingReason != "Plusieurs expéditions détectées")
                {
                    // No-read and multiple-barcode parcels are counted in their own categories.
                    if (!parcel.Dimension.IsValid(_options.MaximumDimension)) parcelCounters.DimensionErrors++;
                    if (parcel.Weight <= 0 || parcel.Weight > _options.MaximumWeight) parcelCounters.ScaleErrors++;
                }
                if (routingReason == "Route de l'expédition") parcelCounters.SortedByWaybill++;
                if (routingReason == "Route du code postal") parcelCounters.SortedByPostalCode++;
                var routed = routingReason is "Route de l'expédition" or "Route du code postal";
                // The sorting decision already applies the effective code 98 policy,
                // including shipment exemptions. Do not reclassify a successful route
                // using raw measurements or a toggle that changed after the decision.
                if (routed &&
                    (!decision.ShipmentNotFound || postalRoute) && decision.Reason == routingReason &&
                    decision.PlcChute != _options.RejectedChute)
                {
                    parcelCounters.SortedWithoutIssue++;
                    successfullySortedByPostalCode = postalRoute;
                }
                _lastDimension = null;
                _lastWeight = null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _databaseConnected = false;
            lock (_gate) _lastError = $"{stage} : {exception.Message}";
            _logger.LogError(exception, "Erreur de traitement sur la ligne {Line}, étape : {Stage}", _options.Id + 1, stage);
            try
            {
                if (code98Sent)
                {
                    _logger.LogWarning("Ligne {Line} : aucun rejet de secours après l'envoi du colis au code 98", _options.Id + 1);
                }
                else
                {
                    var fallbackChute = ResolveClosedChute(_options.RejectedChute);
                    finalDispatchedAt = await SendParcelToPlcAsync(fallbackChute, 1, parcel, fallback: true, token);
                    finalDispatchedChute = fallbackChute;
                    if (fallbackChute == 97 && !recirculationCounted)
                        lock (_gate) parcelCounters.Code97++;
                }
            }
            catch (Exception plcException) { _plcConnected = false; _logger.LogError(plcException, "Automate indisponible"); }
        }
        if (finalDispatchedChute is { } sentChute)
            lock (_gate) parcelCounters.ChuteDispatchCounts[sentChute] = parcelCounters.ChuteDispatchCounts.GetValueOrDefault(sentChute) + 1;
        if (!successfullySortedByPostalCode && classifiedDecision is { } classification)
            lock (_gate)
            {
                if (classification.CountShipmentNotFound)
                {
                    parcelCounters.RejectedShipmentNotFound++;
                    parcelCounters.RecordMissingShipmentCustomer(classification.MissingShipmentCustomer);
                }
                if (classification.CountNoRead) parcelCounters.NoReads++;
            }
        if (code68Count.HasValue && code68CountedAt.HasValue && code68Transfer is not null)
            LogCode68Increment(code68Count.Value, code68CountedAt.Value, code68Transfer, plcAtCode68,
                previousDispatchAtCode68, parcel, classifiedDecision, finalDispatchedChute, finalDispatchedAt);
        _changed();
    }

    private void LogCode68Increment(long count, DateTimeOffset countedAt, DeviceReception transfer,
        DeviceReception? plcAtIncrement, PlcDispatch? previousDispatch, ParcelContext parcel,
        SortDecision? decision, int? finalDispatchedChute, DateTimeOffset? finalDispatchedAt)
    {
        var transferAgeMs = (long)(parcel.CameraTimestamp - transfer.ReceivedAt).TotalMilliseconds;
        _logger.LogWarning(
            "DIAGNOSTIC CODE 68 — ligne {Line}, colis #{ParcelId}, compteur Code 68={Code68Count}, incrémenté à {CountedAt}; " +
            "cause: {TransferTag}=[{TransferValue}] reçu à {TransferAt}, séquence={TransferSequence}, âge à la caméra={TransferAgeMs} ms; " +
            "caméra=[{CameraData}] reçue à {CameraAt}; balance={Weight} reçue à {WeightAt}; " +
            "dimensions={Dimensions} reçues à {DimensionAt}; automate reçu=[{PlcValue}] à {PlcAt}, séquence={PlcSequence}; " +
            "dernier envoi avant incrément: chute={PreviousChute}, à {PreviousSentAt}, colis={PreviousParcelKey}; " +
            "décision: code-barres={Barcode}, raison={Reason}, chute calculée={DecisionChute}, chute automate={DecisionPlcChute}, " +
            "chute finalement envoyée={FinalDispatchedChute}, envoi automate confirmé à {FinalDispatchedAt}",
            _options.Id + 1, parcel.ParcelId, count, FormatTimestamp(countedAt),
            _options.Plc.TransferTag, FormatFrame(transfer.Raw), FormatTimestamp(transfer.ReceivedAt), transfer.Sequence, transferAgeMs,
            FormatFrame(parcel.CameraData), FormatTimestamp(parcel.CameraTimestamp),
            FormatWeight(parcel.Weight, parcel.WeightTimestamp), FormatTimestamp(parcel.WeightTimestamp),
            FormatDimensions(parcel.Dimension, parcel.DimensionTimestamp), FormatTimestamp(parcel.DimensionTimestamp),
            plcAtIncrement is null ? "absent" : FormatFrame(plcAtIncrement.Raw), FormatTimestamp(plcAtIncrement?.ReceivedAt),
            plcAtIncrement?.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absente",
            previousDispatch?.Chute.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absente",
            FormatTimestamp(previousDispatch?.SentAt), previousDispatch?.ParcelKey ?? "absent",
            decision?.Barcode ?? "absent", decision?.Reason ?? "absente",
            decision?.Chute.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absente",
            decision?.PlcChute.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absente",
            finalDispatchedChute?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absente",
            FormatTimestamp(finalDispatchedAt));
    }

    private async Task<DateTimeOffset> SendParcelToPlcAsync(int chute, int repeat, ParcelContext parcel, bool fallback,
        CancellationToken token)
    {
        await _plc.SendChuteAsync(_options.Plc.ChuteTag, chute, repeat, token);
        var sentAt = DateTimeOffset.Now;
        lock (_gate)
        {
            _lastPlcDispatch = new(sentAt, chute,
                Math.Max(0, (long)(sentAt - parcel.CameraTimestamp).TotalMilliseconds),
                ++_dispatchSequence)
            { ParcelKey = $"{parcel.CameraTimestamp.UtcTicks}:{parcel.ParcelId}" };
            _lastDispatchedParcel = parcel;
            _recentPlcDispatches.Enqueue(_lastPlcDispatch);
            _chuteTraffic.Add(_lastPlcDispatch);
            while (_recentPlcDispatches.Count > 256) _recentPlcDispatches.Dequeue();
        }
        _logger.LogInformation(
            "Ligne {Line}, colis #{ParcelId}: envoi automate {Mode} confirmé à {SentAt}; {Tag}={Chute}, répétitions={Repeat}; " +
            "caméra [{CameraData}] reçue à {CameraAt}; balance {Weight} reçue à {WeightAt}; " +
            "dimensions {Dimensions} reçues à {DimensionAt}",
            _options.Id + 1, parcel.ParcelId, fallback ? "rejet de secours" : "normal", FormatTimestamp(sentAt),
            _options.Plc.ChuteTag, chute, repeat, parcel.CameraData, FormatTimestamp(parcel.CameraTimestamp),
            FormatWeight(parcel.Weight, parcel.WeightTimestamp), FormatTimestamp(parcel.WeightTimestamp),
            FormatDimensions(parcel.Dimension, parcel.DimensionTimestamp), FormatTimestamp(parcel.DimensionTimestamp));
        _changed();
        return sentAt;
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) => timestamp?.ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture) ?? "absent";

    private static string FormatTime(DateTimeOffset? timestamp) => timestamp?.ToLocalTime()
        .ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) ?? "absent";

    private static string FormatFrame(string value) => value.Replace("\r", "<CR>", StringComparison.Ordinal)
        .Replace("\n", "<LF>", StringComparison.Ordinal).Replace("\0", "<NUL>", StringComparison.Ordinal);

    private static string FormatWeight(decimal weight, DateTimeOffset? timestamp) => timestamp.HasValue
        ? weight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " lb"
        : "absente";

    private static string FormatDimensions(Dimension dimension, DateTimeOffset? timestamp) => timestamp.HasValue
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{dimension.Length:0.##} x {dimension.Width:0.##} x {dimension.Height:0.##}")
        : "absentes";

    private (TimedValue<Dimension>? Dimension, TimedValue<decimal>? Weight) CaptureMeasurements(
        DateTimeOffset cameraTimestamp, TimeSpan window, bool captureDimension = true, bool captureWeight = true)
    {
        lock (_gate)
        {
            TimedValue<Dimension>? dimension;
            if (!captureDimension)
            {
                dimension = null;
            }
            else if (_dimensionInput is { } dimensionInput && dimensionInput.Sequence > _lastUsedDimensionSequence)
            {
                _lastUsedDimensionSequence = dimensionInput.Sequence;
                var parsed = SensorParsers.ParseDimension(dimensionInput.Raw);
                dimension = parsed is not null ? new(parsed, dimensionInput.ReceivedAt) : null;
                if (dimension is null && _simulation && IsCorrelated(_lastDimension, cameraTimestamp, window))
                    dimension = _lastDimension;
            }
            else
            {
                dimension = _dimensionInput is null && IsCorrelated(_lastDimension, cameraTimestamp, window)
                    ? _lastDimension : null;
            }

            TimedValue<decimal>? weight;
            if (!captureWeight)
            {
                weight = null;
            }
            else if (_scaleInput is { } scaleInput && scaleInput.Sequence > _lastUsedScaleSequence)
            {
                _lastUsedScaleSequence = scaleInput.Sequence;
                var parsed = SensorParsers.ParseWeight(scaleInput.Raw, _options.ScaleProtocol);
                weight = parsed is not null ? new(parsed.Value, scaleInput.ReceivedAt) : null;
            }
            else
            {
                weight = _scaleInput is null && IsCorrelated(_lastWeight, cameraTimestamp, window)
                    ? _lastWeight : null;
            }

            return (dimension, weight);
        }
    }

    private static bool IsCorrelated<T>(TimedValue<T>? value, DateTimeOffset cameraTimestamp, TimeSpan window) =>
        value is not null && (cameraTimestamp - value.Timestamp).Duration() <= window;

    private static bool IsCode68(string? value) =>
        value is not null && string.Equals(value.Trim('\0', ' ', '\r', '\n', '\t'), "68", StringComparison.Ordinal);

    internal static bool IsSmallParcel(Dimension dimension, decimal maximumSide) =>
        maximumSide > 0 && dimension.Length > 0 && dimension.Width > 0 && dimension.Height > 0 &&
        Math.Max(dimension.Length, Math.Max(dimension.Width, dimension.Height)) <= maximumSide;

    internal static bool IsLightParcel(decimal weight, decimal maximumWeight) =>
        maximumWeight > 0 && weight > 0 && weight < maximumWeight;

    internal static decimal NormalizeWeight(decimal weight) => weight < 0 ? -1 : weight;
    internal static decimal NormalizeWeight(decimal weight, decimal? underweightReplacementWeight) =>
        weight == -99.03m && underweightReplacementWeight is > 0
            ? underweightReplacementWeight.Value
            : NormalizeWeight(weight);

    internal const decimal InverseLengthMarginInches = 2m;
    internal static bool IsInverseLengthParcel(Dimension dimension) =>
        dimension.Length > 0 && dimension.Width > 0 &&
        dimension.Width >= dimension.Length + InverseLengthMarginInches;

    internal void RecordScalePresenceForParcel(bool receivedWeight, LineCounters? parcelCounters = null)
    {
        var raiseFault = false;
        lock (_gate)
        {
            if (receivedWeight)
            {
                _consecutiveParcelsWithoutScale = 0;
                return;
            }

            _consecutiveParcelsWithoutScale++;
            if (_consecutiveParcelsWithoutScale >= Math.Max(1, _options.Plc.ScaleFaultParcelThreshold))
            {
                _consecutiveParcelsWithoutScale = 0;
                (parcelCounters ?? _counters).ScaleFaults++;
                raiseFault = true;
            }
        }

        if (raiseFault)
        {
            _scaleFaultRequests.Writer.TryWrite(true);
            _changed();
        }
    }

    private async Task ConsumeScaleFaultRequestsAsync(ChannelReader<bool> reader, CancellationToken token)
    {
        await foreach (var _ in reader.ReadAllAsync(token))
        {
            if (string.IsNullOrWhiteSpace(_options.Plc.ScaleFaultTag))
            {
                _logger.LogWarning("Ligne {Line}: faute balance détectée, mais aucun tag automate n'est configuré", _options.Id + 1);
                continue;
            }

            try
            {
                await SendScaleFaultPulseAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Ligne {Line}: impossible d'envoyer l'impulsion de faute balance sur {Tag}",
                    _options.Id + 1, _options.Plc.ScaleFaultTag);
            }
        }
    }

    private async Task SendScaleFaultPulseAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_options.Plc.ScaleFaultTag))
            throw new InvalidOperationException($"Aucun tag de faute balance n'est configuré pour la ligne {_options.Id + 1}.");

        await _scaleFaultPulseGate.WaitAsync(token);
        var faultRaised = false;
        try
        {
            await _plc.SendChuteAsync(_options.Plc.ScaleFaultTag, 1, 1, token);
            faultRaised = true;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, _options.Plc.ScaleFaultPulseMs)), token);
            await _plc.SendChuteAsync(_options.Plc.ScaleFaultTag, 0, 1, token);
            faultRaised = false;
            _logger.LogWarning("Ligne {Line}: impulsion de faute balance envoyée sur {Tag}", _options.Id + 1, _options.Plc.ScaleFaultTag);
        }
        finally
        {
            if (faultRaised)
            {
                try { await _plc.SendChuteAsync(_options.Plc.ScaleFaultTag, 0, 1, CancellationToken.None); }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Ligne {Line}: impossible de remettre le tag {Tag} à zéro",
                        _options.Id + 1, _options.Plc.ScaleFaultTag);
                }
            }
            _scaleFaultPulseGate.Release();
        }
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(token))
        {
            _databaseConnected = await _repository.PingAsync(token);
            _plcConnected = await _plc.PingAsync(token);
            if (_automaticCounterReset) ResetCountersIfDue(DateTime.Now);
            _changed();
        }
    }

    public void ResetCountersIfDue(DateTime now)
    {
        if (!Running || DateOnly.FromDateTime(now) <= _lastReset || TimeOnly.FromDateTime(now) < _options.EndOfDay) return;
        ResetCounters();
        lock (_gate)
        {
            _productionCounters = new();
            _maintenanceCounters = new();
        }
        _lastReset = DateOnly.FromDateTime(now);
    }

    public LineSnapshot Snapshot()
    {
        lock (_gate)
        {
            AccumulateSortingTime(DateTimeOffset.UtcNow);
            var counters = _counters.Copy();
            var connections = _simulation
                ? new ConnectionState(false, false, false, _databaseConnected, false, true, _repository.IsSimulation)
                : new ConnectionState(_cameraReceiver?.Connected == true || _cameraClient?.Connected == true,
                    _dimensionReceiver?.Connected == true || _dimensionClient?.Connected == true,
                    _scaleReceiver?.Connected == true || _scaleClient?.Connected == true, _databaseConnected,
                    _plc.IsConnected && (_plc is not IPlcReadback readback || readback.ReadsHealthy), false, _repository.IsSimulation);
            return new(_options.Id, _options.Name, Running, connections, counters, _lastDecision, _lastError, DateTimeOffset.Now,
                _options.ValidateDimensionsAndWeight, _cameraInput, _dimensionInput, _scaleInput, _plcInput,
                _options.Plc.ChuteTag, _plc is IPlcReadback, _plcTransferInput, _options.Plc.TransferTag,
                _plc is IPlcReadback && !string.IsNullOrWhiteSpace(_options.Plc.TransferTag), _lastPlcDispatch, _maintenance, _productionCounters.Copy(), _maintenanceCounters.Copy(),
                connections.Plc && !connections.Simulated ? _scaleFaultActive : null, _lastParcelReceivedAt,
                _recentPlcDispatches.ToArray(), _chuteTraffic.Counts(DateTimeOffset.UtcNow));
        }
    }
}
