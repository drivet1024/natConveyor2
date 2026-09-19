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
    public event Action? Changed;

    public ConveyorSupervisor(IOptions<ConveyorOptions> options, IConveyorRepository repository,
        SortEngine sortEngine, ILoggerFactory loggerFactory)
    {
        var configuration = options.Value;
        var activeLines = configuration.GetConfiguredLines().ToArray();
        var primaryLine = activeLines.First();
        _plc = configuration.Simulation
            ? new SimulationPlcGateway(loggerFactory.CreateLogger<SimulationPlcGateway>())
            : string.Equals(primaryLine.Plc.Protocol, "Tcp", StringComparison.OrdinalIgnoreCase)
                ? new TcpPlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<TcpPlcGateway>())
                : new DdePlcGateway(primaryLine.Plc, loggerFactory.CreateLogger<DdePlcGateway>());
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
