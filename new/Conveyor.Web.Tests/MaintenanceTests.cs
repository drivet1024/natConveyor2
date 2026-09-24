using Conveyor.Web.Domain;
using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class MaintenanceTests
{
    [Fact]
    public async Task InFlightParcelRetainsItsOriginalCountersAndResetsOnlyAffectActiveMode()
    {
        var repository = new SimulationConveyorRepository();
        var plc = new BlockingPlc();
        var controller = new LineController(new() { CorrelationDelayMs = 0 }, true, repository, plc, false,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLogger.Instance, () => { });
        try
        {
            var parcel = controller.SimulateAsync("12345678901", new(12, 8, 5), 4);
            await plc.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            controller.SetMaintenance(true);
            plc.Release.TrySetResult();
            await parcel;
            var state = controller.Snapshot();
            Assert.True(state.Maintenance);
            Assert.Equal(0, state.Counters.TotalParcels);
            Assert.Equal(1, state.ProductionCounters!.TotalParcels);
            Assert.Equal(1, state.ProductionCounters.DatabaseInserts);
            Assert.Equal(0, state.MaintenanceCounters!.DatabaseInserts);
            await controller.SimulateAsync("12345678901", new(12, 8, 5), 4);
            Assert.Equal(1, controller.Snapshot().MaintenanceCounters!.DatabaseInserts);
            controller.SetMaintenance(false);
            Assert.Equal(1, controller.Snapshot().Counters.TotalParcels);
            controller.ResetCounters();
            Assert.Equal(0, controller.Snapshot().ProductionCounters!.TotalParcels);
            Assert.Equal(1, controller.Snapshot().MaintenanceCounters!.TotalParcels);
            controller.SetMaintenance(true);
            Assert.Equal(1, controller.Snapshot().Counters.TotalParcels);
        }
        finally { plc.Release.TrySetResult(); await controller.StopAsync(); }
    }

    [Fact]
    public async Task SuccessfulStartSelectsModeForBothLinesAndStopKeepsIt()
    {
        var config = new ConveyorOptions
        {
            General = new() { ConveyorId = 7 }, Lines = [new() { Id = 0 }, new() { Id = 1 }]
        };
        config.ApplyGlobalSorting();
        var repository = new SimulationConveyorRepository();
        using var supervisor = new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(config), repository,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance, new TestConfigurationEditor());
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.SetConveyorMotionAsync(true, null, true));
        Assert.False(supervisor.Maintenance);
        await supervisor.StartLineAsync(0);
        try
        {
            Assert.False(supervisor.CanChangeOperatingMode);
            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.SetConveyorMotionAsync(true, null, true));
            supervisor.RecordPlcTagChange(config.General!.ConveyorStartTag, "1");
            Assert.False(supervisor.CanChangeOperatingMode);
            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.SetConveyorMotionAsync(true, null, true));
            Assert.False(supervisor.Maintenance);
            supervisor.RecordPlcTagChange(config.General.ConveyorStartTag, "0");
            Assert.True(supervisor.CanChangeOperatingMode);
            await supervisor.SetConveyorMotionAsync(true, null, true);
            Assert.True(supervisor.Maintenance);
            Assert.All(supervisor.GetSnapshots(), line => Assert.True(line.Maintenance));
            supervisor.RecordPlcTagChange(config.General.ConveyorStartTag, "1");
            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.SetConveyorMotionAsync(true, null));
            await supervisor.SetConveyorMotionAsync(false, 0);
            Assert.True(supervisor.Maintenance);
            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.SetConveyorMotionAsync(true, null));
            supervisor.RecordPlcTagChange(config.General.ConveyorStartTag, "0");
            await supervisor.SetConveyorMotionAsync(true, null);
            Assert.False(supervisor.Maintenance);
            Assert.All(supervisor.GetSnapshots(), line => Assert.False(line.Maintenance));
        }
        finally { await supervisor.StopLineAsync(0); }
    }

    private sealed class BlockingPlc : IPlcGateway
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(true);
        public async Task SendChuteAsync(string tag, int chute, int repeat, CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }
}
