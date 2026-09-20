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
    public int CurrentShiftId => _configuration.General!.ShiftId;
    public bool? ConveyorRunning { get; private set; }

    public async Task<ConveyorActionResult> SetConveyorMotionAsync(bool start, int? cause)
    {
        if (!await _motionGate.WaitAsync(0)) throw new InvalidOperationException("Une commande convoyeur est déjà en cours.");
        try
        {
            return await ConveyorMotion.ExecuteAsync(_plc, _repository, _configuration.General?.ConveyorId,
                _configuration.Simulation, start, cause, _logger);
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
        SortEngine sortEngine, ILoggerFactory loggerFactory, IConfigurationEditor editor)
    {
        var configuration = options.Value;
        _configuration = configuration;
        _editor = editor;
        _repository = repository;
        _logger = loggerFactory.CreateLogger<ConveyorSupervisor>();
        var activeLines = configuration.GetConfiguredLines().ToArray();
        var primaryLine = activeLines.First();
        _plc = configuration.Simulation
            ? new SimulationPlcGateway(loggerFactory.CreateLogger<SimulationPlcGateway>())
            : string.Equals(primaryLine.Plc.Protocol, "Tcp", StringComparison.OrdinalIgnoreCase)
                ? new TcpPlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<TcpPlcGateway>())
                : new DdePlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<DdePlcGateway>(),
                    activeLines.SelectMany(line => new[] { line.Plc.ChuteTag, line.Plc.TransferTag })
                        .Where(tag => !string.IsNullOrWhiteSpace(tag)).Append(ConveyorMotion.MotionTag));
        var logger = loggerFactory.CreateLogger<ConveyorSupervisor>();
        foreach (var line in activeLines.Where(line => !line.Enabled))
            logger.LogInformation("Ligne {Line} : démarrage automatique désactivé; appareils non connectés jusqu’au START", line.Id + 1);
        _autoStartIds = activeLines.Where(line => line.Enabled).Select(line => line.Id).ToHashSet();
        _lines = activeLines.ToDictionary(line => line.Id, line =>
        {
            return new LineController(line, configuration.Simulation, repository, _plc,
                line.Id == primaryLine.Id, sortEngine,
                loggerFactory.CreateLogger($"Conveyor.Line.{line.Id}"), () => Changed?.Invoke());
        });
        if (_plc is DdePlcGateway dde) dde.TagChanged += RecordPlcTagChange;
    }

    internal void RecordPlcTagChange(string tag, string value)
    {
        if (string.Equals(tag, ConveyorMotion.MotionTag, StringComparison.OrdinalIgnoreCase))
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var line in _lines.Where(pair => _autoStartIds.Contains(pair.Key)).Select(pair => pair.Value))
            await line.StartAsync();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_lines.Values.Select(line => line.StopAsync()));
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        if (_plc is DdePlcGateway dde) dde.TagChanged -= RecordPlcTagChange;
        (_plc as IDisposable)?.Dispose();
        base.Dispose();
    }

    public IReadOnlyList<LineSnapshot> GetSnapshots() => _lines.Values.Select(line => line.Snapshot()).OrderBy(x => x.LineId).ToArray();
    public Task StartLineAsync(int lineId) => Get(lineId).StartAsync();
    public async Task RestartLineAsync(int lineId)
    {
        var line = Get(lineId);
        await line.StopAsync();
        await line.StartAsync();
    }
    public Task StopLineAsync(int lineId) => Get(lineId).StopAsync();
    public void ResetCounters(int lineId) => Get(lineId).ResetCounters();
    public void SetCode98Enabled(int lineId, bool enabled) => Get(lineId).SetCode98Enabled(enabled);
    public Task SimulateParcelAsync(int lineId, string cameraData, Dimension dimension, decimal weight) => Get(lineId).SimulateAsync(cameraData, dimension, weight);
    private LineController Get(int lineId) => _lines.TryGetValue(lineId, out var line) ? line : throw new KeyNotFoundException($"Ligne {lineId} inconnue.");
}
