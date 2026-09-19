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
    private SortDecision? _lastDecision;
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

    public async Task StartAsync(bool connectPlc = true)
    {
        if (Running) return;
        _stopping = new CancellationTokenSource();
        var token = _stopping.Token;
        _databaseConnected = await _repository.PingAsync(token);
        if (_controlsPlcConnection && connectPlc)
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
                _cameraClient = new(_options.CameraHost, _options.CameraPort, "\r", _logger);
                _tasks.Add(_cameraClient.RunAsync(camera.Writer, token));
            }
            else
            {
                _cameraReceiver = new(_options.CameraPort, "\r", _logger);
                _tasks.Add(_cameraReceiver.RunAsync(camera.Writer, token));
            }
            if (_options.ScaleConnectMode)
            {
                _scaleClient = new(_options.ScaleHost, _options.ScalePort, "\r\n", _logger);
                _tasks.Add(_scaleClient.RunAsync(scale.Writer, token));
            }
            else
            {
                _scaleReceiver = new(_options.ScalePort, "\r\n", _logger);
                _tasks.Add(_scaleReceiver.RunAsync(scale.Writer, token));
            }
            if (_options.DimensionConnectMode)
            {
                _dimensionClient = new(_options.DimensionHost, _options.DimensionPort, "\u0003", _logger);
                _tasks.Add(_dimensionClient.RunAsync(dimension.Writer, token));
            }
            else
            {
                _dimensionReceiver = new(_options.DimensionPort, "\u0003", _logger);
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
        lock (_gate) _counters = new LineCounters();
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
        if (_options.CorrelationDelayMs > 0) await Task.Delay(_options.CorrelationDelayMs, token);
        TimedValue<Dimension>? dimension;
        TimedValue<decimal>? weight;
        lock (_gate) { dimension = _lastDimension; weight = _lastWeight; }
        var window = TimeSpan.FromMilliseconds(_options.CorrelationWindowMs);
        var parcel = new ParcelContext(frame, timestamp,
            dimension is not null && (timestamp - dimension.Timestamp).Duration() <= window ? dimension.Value : Dimension.Missing,
            dimension?.Timestamp,
            weight is not null && (timestamp - weight.Timestamp).Duration() <= window ? weight.Value : -1,
            weight?.Timestamp);
        try
        {
            var decision = await _sortEngine.DecideAsync(_options, parcel, token);
            _databaseConnected = true;
            await _plc.SendChuteAsync(_options.Plc.ChuteTag, decision.PlcChute, _options.Plc.SendCount, token);
            _plcConnected = true;
            await _repository.SaveScanAsync(_options.Id, parcel, decision, token);
            lock (_gate)
            {
                _lastDecision = decision;
                _lastError = null;
                _counters.DatabaseInserts++;
                if (decision.Chute == _options.RejectedChute || decision.Chute == 99) _counters.Rejected++;
                if (decision.Chute == _options.NoReadChute) _counters.NoReads++;
                if (!parcel.Dimension.IsValid(_options.MaximumDimension)) _counters.DimensionErrors++;
                if (parcel.Weight <= 0 || parcel.Weight > _options.MaximumWeight) _counters.ScaleErrors++;
                if (decision.Reason == "Route de l'expédition") _counters.SortedByWaybill++;
                if (decision.Reason == "Route du code postal") _counters.SortedByPostalCode++;
                _lastDimension = null;
                _lastWeight = null;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _databaseConnected = false;
            lock (_gate) _lastError = exception.Message;
            _logger.LogError(exception, "Erreur de traitement sur la ligne {Line}; envoi vers le rejet", _options.Id);
            try { await _plc.SendChuteAsync(_options.Plc.ChuteTag, _options.RejectedChute, 1, token); }
            catch (Exception plcException) { _plcConnected = false; _logger.LogError(plcException, "Automate indisponible"); }
        }
        _changed();
    }

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
                TotalParcels = _counters.TotalParcels, Rejected = _counters.Rejected, NoReads = _counters.NoReads,
                DimensionErrors = _counters.DimensionErrors, ScaleErrors = _counters.ScaleErrors,
                SortedByWaybill = _counters.SortedByWaybill, SortedByPostalCode = _counters.SortedByPostalCode,
                DatabaseInserts = _counters.DatabaseInserts
            };
            var connections = _simulation
                ? new ConnectionState(false, false, false, _databaseConnected, false, true, _repository.IsSimulation)
                : new ConnectionState(_cameraReceiver?.Connected == true || _cameraClient?.Connected == true,
                    _dimensionReceiver?.Connected == true || _dimensionClient?.Connected == true,
                    _scaleReceiver?.Connected == true || _scaleClient?.Connected == true, _databaseConnected, _plc.IsConnected, false, _repository.IsSimulation);
            return new(_options.Id, _options.Name, Running, connections, counters, _lastDecision, _lastError, DateTimeOffset.Now);
        }
    }
}
