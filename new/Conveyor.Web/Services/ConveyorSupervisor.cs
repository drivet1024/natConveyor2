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
    private readonly ConveyorOptions _configuration;
    private readonly IConveyorRepository _repository;
    private readonly ILogger _logger;
    public int CurrentShiftId => _configuration.General!.ShiftId;

    public async Task SetShiftAsync(int shiftId)
    {
        if (_configuration.General?.DepotId != 2)
            throw new InvalidOperationException("Le choix de shift est réservé au dépôt 2.");
        var shifts = await _repository.GetShiftIdsAsync(CancellationToken.None);
        if (!shifts.Contains(shiftId))
            throw new InvalidOperationException("Ce shift n’est pas disponible dans les routes configurées.");
        foreach (var line in _configuration.Lines) line.ShiftId = shiftId;
        _configuration.General.ShiftId = shiftId;
        _logger.LogInformation("Dépôt 2 : shift {Shift} sélectionné pour les deux lignes", shiftId);
        Changed?.Invoke();
    }

    public event Action? Changed;

    public ConveyorSupervisor(IOptions<ConveyorOptions> options, IConveyorRepository repository,
        SortEngine sortEngine, ILoggerFactory loggerFactory)
    {
        var configuration = options.Value;
        _configuration = configuration;
        _repository = repository;
        _logger = loggerFactory.CreateLogger<ConveyorSupervisor>();
        var activeLines = configuration.GetConfiguredLines().ToArray();
        var primaryLine = activeLines.First();
        _plc = configuration.Simulation
            ? new SimulationPlcGateway(loggerFactory.CreateLogger<SimulationPlcGateway>())
            : string.Equals(primaryLine.Plc.Protocol, "Tcp", StringComparison.OrdinalIgnoreCase)
                ? new TcpPlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<TcpPlcGateway>())
                : new DdePlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<DdePlcGateway>());
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
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var line in _lines.Where(pair => _autoStartIds.Contains(pair.Key)).Select(pair => pair.Value))
            await line.StartAsync(connectPlc: false);
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
