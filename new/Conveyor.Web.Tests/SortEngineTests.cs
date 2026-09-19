using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class SortEngineTests
{
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
    private static ParcelContext Parcel(string camera) => new(camera, DateTimeOffset.Now, new Dimension(12, 8, 5), DateTimeOffset.Now, 4.75m, DateTimeOffset.Now);

    private sealed class FakeRepository : IConveyorRepository
    {
        public bool IsSimulation => true;
        public Task<Shipment?> FindShipmentAsync(string barcode, CancellationToken token) => Task.FromResult<Shipment?>(
            barcode.StartsWith("123456789", StringComparison.Ordinal) ? new Shipment(barcode[..9], 1, 10, false) : null);
        public Task<int?> FindChuteForRouteAsync(int shiftId, int routeId, CancellationToken token) => Task.FromResult<int?>(4);
        public Task<int?> FindChuteForPostalCodeAsync(int shiftId, string postalCode, CancellationToken token) => Task.FromResult<int?>(7);
        public Task<bool> ShouldUseExceptionChuteAsync(string codeType, string barcode, int retryLimit, CancellationToken token) => Task.FromResult(true);
        public Task ClearExceptionCodeAsync(string codeType, string barcode, CancellationToken token) => Task.CompletedTask;
        public Task SaveScanAsync(int lineId, ParcelContext parcel, SortDecision decision, CancellationToken token) => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(true);
        public Task<(long Parcels, long PostalCodes)> GetReferenceCountsAsync(CancellationToken token) => Task.FromResult((0L, 0L));
    }
}
