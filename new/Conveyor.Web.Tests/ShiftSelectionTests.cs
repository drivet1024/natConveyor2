using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Tests;

public sealed class ShiftSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedConveyorCountsFollowTheirConfiguredTags(bool customTags)
    {
        var options = CreateOptions(1);
        if (customTags)
        {
            options.General!.FullChutesTag = "FULL_CUSTOM";
            options.General.Code42Tag = "CODE42_CUSTOM";
        }
        using var supervisor = CreateSupervisor(options);
        Assert.Null(supervisor.FullChutesCount);
        Assert.Null(supervisor.Code42Count);
        var notifications = 0;
        supervisor.Changed += () => notifications++;
        supervisor.RecordPlcTagChange(options.General!.FullChutesTag.ToLowerInvariant(), " 3\0\r\n");
        supervisor.RecordPlcTagChange(options.General.Code42Tag, "42");
        Assert.Equal(3, supervisor.FullChutesCount);
        Assert.Equal(42, supervisor.Code42Count);
        Assert.Equal(2, notifications);
        supervisor.RecordPlcTagChange("UNRELATED", "9");
        Assert.Equal(2, notifications);
        supervisor.RecordPlcTagChange(options.General.FullChutesTag, "0");
        Assert.Equal(0, supervisor.FullChutesCount);
        Assert.Equal(42, supervisor.Code42Count);
        supervisor.RecordPlcTagChange(options.General.FullChutesTag, "invalid");
        supervisor.RecordPlcTagChange(options.General.Code42Tag, "-1");
        Assert.Null(supervisor.FullChutesCount);
        Assert.Null(supervisor.Code42Count);
    }

    [Fact]
    public async Task SelectionUpdatesBothLinesAndNotifiesObservers()
    {
        var options = CreateOptions(2);
        using var supervisor = CreateSupervisor(options);
        var notifications = 0;
        supervisor.Changed += () => notifications++;
        await supervisor.SetShiftAsync(2);
        Assert.Equal(2, supervisor.CurrentShiftId);
        Assert.All(options.Lines, line => Assert.Equal(2, line.ShiftId));
        Assert.Equal(1, notifications);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.SetShiftAsync(99));
        Assert.Equal(2, supervisor.CurrentShiftId);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task OtherDepotsCanChangeToAnAvailableLocalShift()
    {
        var options = CreateOptions(1);
        using var supervisor = CreateSupervisor(options);
        await supervisor.SetShiftAsync(2);
        Assert.Equal(2, supervisor.CurrentShiftId);
        Assert.All(options.Lines, line => Assert.Equal(2, line.ShiftId));
    }

    [Fact]
    public void ConveyorRunningReflectsConfiguredMotionTagValue()
    {
        var options = CreateOptions(1);
        options.General!.ConveyorStartTag = "START_CUSTOM";
        using var supervisor = CreateSupervisor(options);
        Assert.Null(supervisor.ConveyorRunning);
        supervisor.RecordPlcTagChange("START_CUSTOM", "1");
        Assert.True(supervisor.ConveyorRunning);
        supervisor.RecordPlcTagChange("START_CUSTOM", "0");
        Assert.False(supervisor.ConveyorRunning);
        supervisor.RecordPlcTagChange("OTHER_TAG", "1");
        Assert.False(supervisor.ConveyorRunning);
    }

    [Fact]
    public async Task Chute4FullStateMustRemainStableBeforeStopAndGoChanges()
    {
        var options = CreateOptions(1);
        options.General!.Chute4FullTag = "CHUTE_4_FULL";
        options.General.StopAndGoTag = "STOP_AND_GO";
        options.General.StopAndGoDelaySeconds = 1;
        var gateway = new RecordingPlcGateway();
        using var supervisor = CreateSupervisor(options, gateway);

        gateway.Emit("STOP_AND_GO", "0");
        Assert.False(supervisor.Chute4AlarmActive);
        gateway.Emit("CHUTE_4_FULL", "1");
        Assert.True(supervisor.Chute4AlarmActive);
        await Task.Delay(300);
        gateway.Emit("CHUTE_4_FULL", "0");
        Assert.False(supervisor.Chute4AlarmActive);
        await Task.Delay(900);
        Assert.Empty(gateway.Writes);

        gateway.Emit("CHUTE_4_FULL", "1");
        Assert.True(supervisor.Chute4AlarmActive);
        await Task.Delay(600);
        gateway.Emit("CHUTE_4_FULL", "1");
        await Task.Delay(600);
        Assert.Equal(("STOP_AND_GO", 1), Assert.Single(gateway.Writes));

        gateway.Emit("STOP_AND_GO", "1");
        gateway.Emit("CHUTE_4_FULL", "0");
        await Task.Delay(1_200);
        Assert.Equal([1, 0], gateway.Writes.Select(write => write.Value).ToArray());
        Assert.True(supervisor.Chute4AlarmActive);
        gateway.Emit("STOP_AND_GO", "0");
        Assert.False(supervisor.Chute4AlarmActive);
    }

    [Fact]
    public void Chute4CounterCountsOnlyObservedRisingEdges()
    {
        var options = CreateOptions(1);
        options.General!.Chute4FullTag = "CHUTE_4_FULL";
        options.General.StopAndGoTag = "STOP_AND_GO";
        using var supervisor = CreateSupervisor(options);
        Assert.Null(supervisor.Chute4FullTransitions);
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "1");
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "1");
        Assert.Equal(0L, supervisor.Chute4FullTransitions);
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "0");
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "1");
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "1");
        Assert.Equal(1L, supervisor.Chute4FullTransitions);
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "0");
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "invalid");
        Assert.Null(supervisor.Chute4FullTransitions);
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "1");
        Assert.Equal(1L, supervisor.Chute4FullTransitions);
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "0");
        supervisor.RecordPlcTagChange("CHUTE_4_FULL", "1");
        supervisor.RecordPlcTagChange("OTHER", "1");
        Assert.Equal(2L, supervisor.Chute4FullTransitions);
    }

    [Theory]
    [InlineData("FULL", "", 5)]
    [InlineData("", "STOP", 5)]
    [InlineData("SAME", "same", 5)]
    [InlineData("FULL", "STOP", 0)]
    public void StopAndGoSettingsRejectIncompleteOrUnsafeValues(string input, string output, int delay)
    {
        var settings = new GeneralOptions { Chute4FullTag = input, StopAndGoTag = output, StopAndGoDelaySeconds = delay };
        Assert.NotNull(settings.StopAndGoValidationError());
    }

    private static ConveyorOptions CreateOptions(int depot)
    {
        var options = new ConveyorOptions
        {
            Simulation = true,
            General = new() { DepotId = depot, ShiftId = 1 },
            Lines = [new() { Id = 0 }, new() { Id = 1 }]
        };
        options.ApplyGlobalSorting();
        return options;
    }

    private static ConveyorSupervisor CreateSupervisor(ConveyorOptions options, IPlcGateway? gateway = null)
    {
        var repository = new SimulationConveyorRepository();
        return new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(options), repository,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance,
            new TestConfigurationEditor(), plcGateway: gateway);
    }

    private sealed class RecordingPlcGateway : IPlcGateway, IPlcReadback
    {
        public bool IsConnected => true;
        public bool ReadsHealthy => true;
        public event Action<string, string>? TagChanged;
        public System.Collections.Concurrent.ConcurrentQueue<(string Tag, int Value)> Writes { get; } = new();
        public void Emit(string tag, string value) => TagChanged?.Invoke(tag, value);
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(true);
        public Task SendChuteAsync(string tag, int chute, int repeat, CancellationToken token)
        {
            Writes.Enqueue((tag, chute));
            return Task.CompletedTask;
        }
    }
}
