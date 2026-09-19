using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class SortEngineTests
{
    [Fact]
    public async Task Database_counter_reads_current_history_total_including_decreases()
    {
        var repository = new FakeRepository { ScanCount = 1234 };
        using var metrics = new DatabaseMetricsService(repository, NullLogger<DatabaseMetricsService>.Instance);
        await metrics.RefreshAsync();
        Assert.Equal(1234, metrics.Current.Scans);
        repository.ScanCount = 12;
        await metrics.RefreshAsync();
        Assert.Equal(12, metrics.Current.Scans);
    }

    [Fact]
    public async Task Database_counter_keeps_last_total_when_read_fails()
    {
        var repository = new FakeRepository { ScanCount = 1234 };
        using var metrics = new DatabaseMetricsService(repository, NullLogger<DatabaseMetricsService>.Instance);
        await metrics.RefreshAsync();
        repository.FailCounts = true;
        await metrics.RefreshAsync();
        Assert.Equal(1234, metrics.Current.Scans);
        Assert.False(metrics.Current.Connected);
    }

    [Fact]
    public async Task Known_waybill_uses_configured_route()
    {
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var result = await engine.DecideAsync(Line(), Parcel("12345678901,H2X1Y4"), CancellationToken.None);
        Assert.Equal(4, result.Chute);
        Assert.Equal("Route de l'expédition", result.Reason);
    }

    [Fact]
    public async Task Unknown_waybill_can_fall_back_to_postal_code()
    {
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var result = await engine.DecideAsync(Line(), Parcel("99999999999,H2X1Y4"), CancellationToken.None);
        Assert.Equal(7, result.Chute);
        Assert.Equal("Route du code postal", result.Reason);
    }

    [Fact]
    public async Task Multiple_known_waybills_use_safety_chute_99_and_plc_reject()
    {
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var result = await engine.DecideAsync(Line(), Parcel("12345678901,12345678902"), CancellationToken.None);
        Assert.Equal(99, result.Chute);
        Assert.Equal(16, result.PlcChute);
    }

    [Fact]
    public async Task Invalid_measurements_use_code_98()
    {
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var parcel = Parcel("12345678901") with { Weight = -1, Dimension = Dimension.Missing };
        var result = await engine.DecideAsync(Line(), parcel, CancellationToken.None);
        Assert.Equal(98, result.Chute);
    }

    private static LineOptions Line() => new() { Id = 0, ShiftId = 1, RejectedChute = 16, NoReadChute = 1 };
    [Theory]
    [InlineData(true, "12345678901", 0, 12, 98)]
    [InlineData(true, "12345678901", 5, 0, 98)]
    [InlineData(true, "99999999999,H2X1Y4", 0, 12, 98)]
    [InlineData(true, "?", 0, 0, 98)]
    [InlineData(false, "12345678901", 0, 0, 4)]
    [InlineData(false, "99999999999,H2X1Y4", 0, 0, 7)]
    [InlineData(false, "?", 0, 0, 1)]
    [InlineData(true, "12345678901", 5, 12, 4)]
    [InlineData(true, "12345678901,12345678902", 0, 0, 99)]
    public async Task Code98_switch_preserves_original_route_when_disabled(bool enabled, string camera, int weight, int length, int expected)
    {
        var line = Line();
        line.ValidateDimensionsAndWeight = enabled;
        var engine = new SortEngine(new FakeRepository(), NullLogger<SortEngine>.Instance);
        var parcel = Parcel(camera) with { Weight = weight, Dimension = new Dimension(length, 8, 5) };
        var result = await engine.DecideAsync(line, parcel, CancellationToken.None);
        Assert.Equal(expected, result.Chute);
        Assert.Equal(expected == 99 ? line.RejectedChute : expected, result.PlcChute);
    }

    [Fact]
    public async Task Code98_toggle_is_shared_and_counter_resets()
    {
        var config = new ConveyorOptions { Simulation = true, Lines = [new() { Id = 0, CorrelationDelayMs = 0 }, new() { Id = 1 }] };
        config.ApplyGlobalSorting();
        var repo = new FakeRepository();
        using var supervisor = new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(config), repo,
            new SortEngine(repo, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance);
        try
        {
            supervisor.SetCode98Enabled(false);
            Assert.All(supervisor.GetSnapshots(), line => Assert.False(line.Code98Enabled));
            await supervisor.SimulateParcelAsync(0, "12345678901", Dimension.Missing, -1);
            Assert.Equal(0, supervisor.GetSnapshots()[0].Counters.Code98);
            supervisor.SetCode98Enabled(true);
            Assert.All(supervisor.GetSnapshots(), line => Assert.True(line.Code98Enabled));
            await supervisor.SimulateParcelAsync(0, "12345678901", Dimension.Missing, -1);
            Assert.Equal(1, supervisor.GetSnapshots()[0].Counters.Code98);
            supervisor.ResetCounters(0);
            Assert.Equal(0, supervisor.GetSnapshots()[0].Counters.Code98);
        }
        finally { await supervisor.StopLineAsync(0); }
    }
    private static ParcelContext Parcel(string camera) => new(camera, DateTimeOffset.Now, new Dimension(12, 8, 5), DateTimeOffset.Now, 4.75m, DateTimeOffset.Now);

    private sealed class FakeRepository : IConveyorRepository
    {
        public long ScanCount { get; set; }
        public bool FailCounts { get; set; }
        public bool IsSimulation => true;
        public Task<Shipment?> FindShipmentAsync(string barcode, CancellationToken token) => Task.FromResult<Shipment?>(
            barcode.StartsWith("123456789", StringComparison.Ordinal) ? new Shipment(barcode[..9], 1, 10, false) : null);
        public Task<int?> FindChuteForRouteAsync(int shiftId, int routeId, CancellationToken token) => Task.FromResult<int?>(4);
        public Task<int?> FindChuteForPostalCodeAsync(int shiftId, string postalCode, CancellationToken token) => Task.FromResult<int?>(7);
        public Task<bool> ShouldUseExceptionChuteAsync(string codeType, string barcode, int retryLimit, CancellationToken token) => Task.FromResult(true);
        public Task ClearExceptionCodeAsync(string codeType, string barcode, CancellationToken token) => Task.CompletedTask;
        public Task SaveScanAsync(int lineId, ParcelContext parcel, SortDecision decision, CancellationToken token) => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(true);
        public Task<(long Parcels, long PostalCodes, long Scans)> GetReferenceCountsAsync(CancellationToken token) =>
            FailCounts ? Task.FromException<(long, long, long)>(new IOException("Database unavailable"))
                : Task.FromResult((0L, 0L, ScanCount));
    }
}
