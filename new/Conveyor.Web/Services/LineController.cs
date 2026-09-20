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
    public void RecordPlcReception(string value)
    {
        lock (_gate)
        {
            if (_plcInput?.Raw == value) return;
            _plcInput = new(value, DateTimeOffset.Now, (_plcInput?.Sequence ?? 0) + 1);
        }
        _changed();
    }
    private DeviceReception? _cameraInput;
    private DeviceReception? _dimensionInput;
    private DeviceReception? _scaleInput;

    private void RecordReception(string device, string frame)
    {
        lock (_gate)
        {
            var previous = device switch { "camera" => _cameraInput, "dimension" => _dimensionInput, _ => _scaleInput };
            var input = new DeviceReception(frame.Length > 4096 ? frame[..4096] : frame,
                DateTimeOffset.Now, (previous?.Sequence ?? 0) + 1, frame.Length > 4096);
            switch (device)
            {
                case "camera": _cameraInput = input; break;
                case "dimension": _dimensionInput = input; break;
                default: _scaleInput = input; break;
            }
        }
        _changed();
    }
    private LineCounters _counters = new();
    private string? _lastError;
    private bool _databaseConnected;
    private bool _plcConnected;
    private DateOnly _lastReset = DateOnly.FromDateTime(DateTime.Now);

    public LineController(LineOptions options, bool simulation, IConveyorRepository repository, IPlcGateway plc,
        bool controlsPlcConnection,
        SortEngine sortEngine, ILogger logger, Action changed)
    {
        _options = options;
        _simulation = simulation;
        _repository = repository;
        _plc = plc;
        _controlsPlcConnection = controlsPlcConnection;
        _sortEngine = sortEngine;
        _logger = logger;
        _changed = changed;
    }

    public bool Running => _stopping is { IsCancellationRequested: false };

    public async Task StartAsync()
    {
        if (Running) return;
        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
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
                _lastError = $"Automate DDE: {exception.Message}";
                _logger.LogError(exception, "Impossible de démarrer la connexion automate pour la ligne principale");
            }
        }
        _plcConnected = _plc.IsConnected;
        if (!_simulation)
        {
            var camera = Channel.CreateBounded<string>(new BoundedChannelOptions(1_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            var dimension = Channel.CreateBounded<string>(1_000);
            var scale = Channel.CreateBounded<string>(1_000);
            _tasks =
            [
                ConsumeCameraAsync(camera.Reader, token),
                ConsumeDimensionsAsync(dimension.Reader, token),
                ConsumeScaleAsync(scale.Reader, token),
                MonitorAsync(token)
            ];
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
        _logger.LogInformation("Ligne {Line} arrêtée", _options.Id);
        _changed();
    }

    public void ResetCounters()
    {
        lock (_gate)
        {
            _counters = new LineCounters();
            _lastDecision = null;
            _plcInput = null;
            _cameraInput = null;
            _dimensionInput = null;
            _scaleInput = null;
            _lastUsedDimensionSequence = 0;
            _lastUsedScaleSequence = 0;
        }
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
        lock (_gate) { _counters.CameraReads++; _counters.TotalParcels++; }
        var window = TimeSpan.FromMilliseconds(_options.CorrelationWindowMs);
        var (dimension, weight) = CaptureMeasurements(timestamp, window);
        if (_options.CorrelationDelayMs > 0) await Task.Delay(_options.CorrelationDelayMs, token);
        var parcel = new ParcelContext(frame, timestamp,
            dimension is not null && (timestamp - dimension.Timestamp).Duration() <= window ? dimension.Value : Dimension.Missing,
            dimension?.Timestamp,
            weight is not null && (timestamp - weight.Timestamp).Duration() <= window ? weight.Value : -1,
            weight?.Timestamp);
        var isNoRead = parcel.CameraData.Contains('?');
        var stage = "calcul de la chute";
        var rejectionCounted = false;
        try
        {
            var decision = await _sortEngine.DecideAsync(_options, parcel, token);
            _databaseConnected = true;
            stage = "envoi de la chute à l’automate (insertion non effectuée)";
            await _plc.SendChuteAsync(_options.Plc.ChuteTag, decision.PlcChute, _options.Plc.SendCount, token);
            _plcConnected = true;
            if (!isNoRead && decision.PlcChute == _options.RejectedChute)
            {
                lock (_gate) _counters.Rejected++;
                rejectionCounted = true;
            }
            stage = "insertion MySQL du scan";
            await _repository.SaveScanAsync(_options.Id, parcel, decision, token);
            lock (_gate)
            {
                _lastDecision = decision;
                _lastError = null;
                _counters.DatabaseInserts++;
                if (decision.Chute == 98) _counters.Code98++;
                if (isNoRead) _counters.NoReads++;
                else
                {
                    // Measurement error rates use read parcels only, excluding no-reads.
                    if (!parcel.Dimension.IsValid(_options.MaximumDimension)) _counters.DimensionErrors++;
                    if (parcel.Weight <= 0 || parcel.Weight > _options.MaximumWeight) _counters.ScaleErrors++;
                }
                if (decision.Reason == "Route de l'expédition") _counters.SortedByWaybill++;
                if (decision.Reason == "Route du code postal") _counters.SortedByPostalCode++;
                _lastDimension = null;
                _lastWeight = null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _databaseConnected = false;
            lock (_gate) _lastError = $"{stage} : {exception.Message}";
            _logger.LogError(exception, "Erreur de traitement sur la ligne {Line}, étape : {Stage}; envoi vers le rejet", _options.Id + 1, stage);
            try
            {
                await _plc.SendChuteAsync(_options.Plc.ChuteTag, _options.RejectedChute, 1, token);
                if (!isNoRead && !rejectionCounted)
                    lock (_gate) _counters.Rejected++;
            }
            catch (Exception plcException) { _plcConnected = false; _logger.LogError(plcException, "Automate indisponible"); }
        }
        _changed();
    }

    private (TimedValue<Dimension>? Dimension, TimedValue<decimal>? Weight) CaptureMeasurements(
        DateTimeOffset cameraTimestamp, TimeSpan window)
    {
        lock (_gate)
        {
            TimedValue<Dimension>? dimension;
            if (_dimensionInput is { } dimensionInput && dimensionInput.Sequence > _lastUsedDimensionSequence)
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
            if (_scaleInput is { } scaleInput && scaleInput.Sequence > _lastUsedScaleSequence)
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

    private async Task MonitorAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(token))
        {
            _databaseConnected = await _repository.PingAsync(token);
            _plcConnected = await _plc.PingAsync(token);
            var now = DateTime.Now;
            if (DateOnly.FromDateTime(now) > _lastReset && TimeOnly.FromDateTime(now) >= _options.EndOfDay)
            {
                ResetCounters();
                _lastReset = DateOnly.FromDateTime(now);
            }
            _changed();
        }
    }

    public LineSnapshot Snapshot()
    {
        lock (_gate)
        {
            var counters = new LineCounters
            {
                CameraReads = _counters.CameraReads, DimensionReads = _counters.DimensionReads, ScaleReads = _counters.ScaleReads,
                TotalParcels = _counters.TotalParcels, Rejected = _counters.Rejected, NoReads = _counters.NoReads, Code98 = _counters.Code98,
                DimensionErrors = _counters.DimensionErrors, ScaleErrors = _counters.ScaleErrors,
                SortedByWaybill = _counters.SortedByWaybill, SortedByPostalCode = _counters.SortedByPostalCode,
                DatabaseInserts = _counters.DatabaseInserts
            };
            var connections = _simulation
                ? new ConnectionState(false, false, false, _databaseConnected, false, true, _repository.IsSimulation)
                : new ConnectionState(_cameraReceiver?.Connected == true || _cameraClient?.Connected == true,
                    _dimensionReceiver?.Connected == true || _dimensionClient?.Connected == true,
                    _scaleReceiver?.Connected == true || _scaleClient?.Connected == true, _databaseConnected, _plc.IsConnected, false, _repository.IsSimulation);
            return new(_options.Id, _options.Name, Running, connections, counters, _lastDecision, _lastError, DateTimeOffset.Now, _options.ValidateDimensionsAndWeight, _cameraInput, _dimensionInput, _scaleInput, _plcInput, _options.Plc.ChuteTag, _plc is DdePlcGateway);
        }
    }
}
