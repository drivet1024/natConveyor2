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
    [InlineData("SAME", "same", 5)]
    [InlineData("FULL", "STOP", 0)]
    public void StopAndGoSettingsRejectIncompleteOrUnsafeValues(string input, string output, int delay)
    {
        var settings = new GeneralOptions { Chute4FullTag = input, StopAndGoTag = output, StopAndGoDelaySeconds = delay };
        Assert.NotNull(settings.StopAndGoValidationError());
    }

    [Fact]
    public void FullChuteAlarmsTrackConfiguredTagsIndependently()
    {
        var options = CreateOptions(1);
        options.General!.SetFullChuteTag(1, "FULL_1");
        options.General.SetFullChuteTag(48, "FULL_48");
        options.General.SetFullChuteTag(2, " ");
        options.General.SetFullChuteTag(4, "FULL_4");
        Assert.Null(options.General.StopAndGoValidationError());
        using var supervisor = CreateSupervisor(options);
        supervisor.RecordPlcTagChange("", "1");
        supervisor.RecordPlcTagChange("UNKNOWN", "1");
        Assert.Empty(supervisor.FullChuteAlarms);
        supervisor.RecordPlcTagChange("FULL_1", "1");
        supervisor.RecordPlcTagChange("FULL_48", "1");
        supervisor.RecordPlcTagChange("FULL_4", "1");
        Assert.Equal(new[] { 1, 4, 48 }, supervisor.FullChuteAlarms);
        supervisor.RecordPlcTagChange("FULL_1", "0");
        supervisor.RecordPlcTagChange("FULL_48", "invalid");
        Assert.Equal(new[] { 4, 48 }, supervisor.FullChuteAlarms);
        supervisor.RecordPlcTagChange("FULL_4", "0");
        supervisor.RecordPlcTagChange("FULL_48", "0");
        Assert.Empty(supervisor.FullChuteAlarms);
    }

    [Fact]
    public async Task SharedStopAndGoWaitsUntilAllConfiguredChutesClear()
    {
        var options = CreateOptions(1);
        options.General!.SetFullChuteTag(1, "FULL_1");
        options.General.SetFullChuteTag(48, "FULL_48");
        options.General.StopAndGoTag = "STOP";
        options.General.StopAndGoDelaySeconds = 1;
        var gateway = new RecordingPlcGateway();
        using var supervisor = CreateSupervisor(options, gateway);
        gateway.Emit("STOP", "0");
        gateway.Emit("FULL_1", "1");
        Assert.Equal(new[] { 1 }, supervisor.FullChutesActive);
        await Task.Delay(1200);
        Assert.Equal(("STOP", 1), Assert.Single(gateway.Writes));
        gateway.Emit("STOP", "1");
        gateway.Emit("FULL_1", "0");
        await Task.Delay(1200);
        Assert.Single(gateway.Writes); // Chute 48 has not reported a state yet.
        Assert.Contains(1, supervisor.FullChuteAlarms);
        Assert.Empty(supervisor.FullChutesActive);
        gateway.Emit("FULL_48", "1");
        Assert.Equal(new[] { 48 }, supervisor.FullChutesActive);
        await Task.Delay(1200);
        Assert.Single(gateway.Writes);
        gateway.Emit("FULL_48", "0");
        await Task.Delay(1200);
        Assert.Equal(new[] { 1, 0 }, gateway.Writes.Select(write => write.Value));
        Assert.Equal(new[] { 1, 48 }, supervisor.FullChuteAlarms);
        Assert.Empty(supervisor.FullChutesActive);
        gateway.Emit("STOP", "0");
        Assert.Empty(supervisor.FullChuteAlarms);
    }

    [Fact]
    public void ConfiguredFullChutesCountOnlyObservedRisingEdges()
    {
        var options = CreateOptions(1);
        options.General!.SetFullChuteTag(3, "FULL_3");
        options.General.SetFullChuteTag(4, "FULL_4");
        options.General.SetFullChuteTag(48, "FULL_48");
        options.General.SetFullChuteTag(2, " ");
        using var supervisor = CreateSupervisor(options);
        Assert.Equal(new[] { 3, 4, 48 }, supervisor.FullChuteTransitions.Keys);
        Assert.All(supervisor.FullChuteTransitions.Values, value => Assert.Null(value));
        supervisor.RecordPlcTagChange("FULL_3", "1");
        Assert.Equal(0L, supervisor.FullChuteTransitions[3]);
        supervisor.RecordPlcTagChange("FULL_3", "0");
        supervisor.RecordPlcTagChange("FULL_3", "1");
        supervisor.RecordPlcTagChange("FULL_3", "1");
        Assert.Equal(1L, supervisor.FullChuteTransitions[3]);
        supervisor.RecordPlcTagChange("FULL_4", "0");
        supervisor.RecordPlcTagChange("FULL_4", "1");
        Assert.Equal(supervisor.Chute4FullTransitions, supervisor.FullChuteTransitions[4]);
        supervisor.RecordPlcTagChange("FULL_3", "invalid");
        Assert.Null(supervisor.FullChuteTransitions[3]);
        supervisor.RecordPlcTagChange("FULL_3", "1");
        Assert.Equal(1L, supervisor.FullChuteTransitions[3]);
        Assert.Null(supervisor.FullChuteTransitions[48]);
    }

    [Fact]
    public void EmptyAndNullFullChuteSettingsAreIgnored()
    {
        var options = CreateOptions(1);
        options.General!.Chute4FullTag = null!;
        options.General.StopAndGoTag = null!;
        options.General.FullChuteTags = new() { [1] = null!, [2] = " ", [3] = "", [48] = " FULL_48 " };
        Assert.Null(options.General.StopAndGoValidationError());
        using (var supervisor = CreateSupervisor(options))
        {
            supervisor.RecordPlcTagChange("", "1");
            Assert.Empty(supervisor.FullChuteAlarms);
            supervisor.RecordPlcTagChange("FULL_48", "1");
            Assert.Equal(new[] { 48 }, supervisor.FullChuteAlarms);
        }
        options.General.FullChuteTags = null!;
        Assert.Null(options.General.StopAndGoValidationError());
        using var emptySupervisor = CreateSupervisor(options);
        Assert.Empty(emptySupervisor.FullChuteAlarms);
        options.General.SetFullChuteTag(1, null);
        Assert.Equal("", options.General.GetFullChuteTag(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankStopAndGoNeverWritesAndKeepsConfiguredChuteAlarms(string? stopTag)
    {
        var options = CreateOptions(1);
        options.General!.StopAndGoTag = stopTag!;
        options.General.StopAndGoDelaySeconds = 1;
        options.General.SetFullChuteTag(3, "FULL_3");
        options.General.SetFullChuteTag(4, " ");
        var gateway = new RecordingPlcGateway();
        using var supervisor = CreateSupervisor(options, gateway);
        Assert.Equal(new[] { 3 }, supervisor.FullChuteTransitions.Keys);
        gateway.Emit("FULL_3", "0");
        gateway.Emit("FULL_3", "1");
        Assert.Equal(new[] { 3 }, supervisor.FullChuteAlarms);
        await Task.Delay(1200);
        Assert.Empty(gateway.Writes);
        gateway.Emit("FULL_3", "0");
        Assert.Empty(supervisor.FullChuteAlarms);
        await Task.Delay(1200);
        Assert.Empty(gateway.Writes);
    }

    [Fact]
    public void ResetClearsChuteStatisticsWithoutClearingActiveAlarm()
    {
        var options = CreateOptions(1);
        options.General!.SetFullChuteTag(4, "FULL_4");
        using var supervisor = CreateSupervisor(options);
        supervisor.RecordPlcTagChange("FULL_4", "0");
        supervisor.RecordPlcTagChange("FULL_4", "1");
        Assert.Equal(1, supervisor.FullChuteTransitions[4]);
        var resetNotified = false;
        supervisor.CountersReset += () => resetNotified = true;
        supervisor.ResetCounters(0);
        Assert.True(resetNotified);
        Assert.Equal(0, supervisor.FullChuteTransitions[4]);
        Assert.Equal(0, supervisor.Chute4FullTransitions);
        Assert.Contains(4, supervisor.FullChuteAlarms);
        supervisor.RecordPlcTagChange("FULL_4", "1");
        Assert.Equal(0, supervisor.FullChuteTransitions[4]);
        supervisor.RecordPlcTagChange("FULL_4", "0");
        supervisor.RecordPlcTagChange("FULL_4", "1");
        Assert.Equal(1, supervisor.FullChuteTransitions[4]);
    }

    [Fact]
    public async Task ManualLineCommandUsesItsOwnTagAndTracksReadback()
    {
        var options = CreateOptions(1);
        options.Lines[0].Plc.StopManuelTag = " MANUAL_1 ";
        options.Lines[1].Plc.StopManuelTag = "MANUAL_2";
        var gateway = new RecordingPlcGateway();
        using var supervisor = CreateSupervisor(options, gateway);
        gateway.Emit("MANUAL_1", "1");
        gateway.Emit("MANUAL_2", "1");
        await supervisor.SetManualLineAsync(0, true);
        Assert.Equal(("MANUAL_1", 0), Assert.Single(gateway.Writes));
        Assert.True(supervisor.GetManualLineState(0));
        Assert.True(supervisor.GetManualLineState(1));
        gateway.Emit("MANUAL_1", "0");
        await supervisor.SetManualLineAsync(0, false);
        Assert.Equal(("MANUAL_1", 1), gateway.Writes.Last());
        Assert.False(supervisor.GetManualLineState(0));
        gateway.Emit("MANUAL_1", "1");
        Assert.True(supervisor.GetManualLineState(0));
        gateway.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => supervisor.SetManualLineAsync(0, false));
        Assert.True(supervisor.GetManualLineState(0));
    }

    [Fact]
    public async Task ManualLineWithoutTagNeverWrites()
    {
        var options = CreateOptions(1);
        var gateway = new RecordingPlcGateway();
        using var supervisor = CreateSupervisor(options, gateway);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.SetManualLineAsync(0, true));
        Assert.Empty(gateway.Writes);
    }

    [Fact]
    public async Task ManualLinePollingReadsBothTagsAndClearsUnavailableStates()
    {
        var options = CreateOptions(1);
        options.Lines[0].Plc.StopManuelTag = " MANUAL_1 ";
        options.Lines[1].Plc.StopManuelTag = "MANUAL_2";
        var gateway = new RecordingPlcGateway { ReadValue = "1" };
        using var supervisor = CreateSupervisor(options, gateway);
        await supervisor.PollManualLinesAsync(default);
        Assert.Equal(2, gateway.ReadCount);
        Assert.True(supervisor.GetManualLineState(0));
        Assert.True(supervisor.GetManualLineState(1));
        gateway.ReadValue = "0";
        await supervisor.PollManualLinesAsync(default);
        Assert.Equal(4, gateway.ReadCount);
        Assert.False(supervisor.GetManualLineState(0));
        gateway.FailReadTag = "MANUAL_1";
        gateway.ReadValue = "1";
        await supervisor.PollManualLinesAsync(default);
        Assert.Null(supervisor.GetManualLineState(0));
        Assert.True(supervisor.GetManualLineState(1));
        gateway.IsConnected = false;
        await supervisor.PollManualLinesAsync(default);
        Assert.Null(supervisor.GetManualLineState(1));
        gateway.IsConnected = true;
        Assert.Null(supervisor.GetManualLineState(1));
        Assert.Empty(gateway.Writes);
    }

    [Theory]
    [InlineData(true, "DÉPART LIGNE")]
    [InlineData(false, "ARRÊT LIGNE")]
    [InlineData(null, "ÉTAT INCONNU")]
    public void ManualButtonLabelMatchesRawTag(bool? state, string label) =>
        Assert.Equal(label, Conveyor.Web.Components.Layout.ManualLineButton.Label(state));

    [Fact]
    public async Task RecirculationWritesOneAndReadsUntilAutomateReturnsZero()
    {
        var options = CreateOptions(1);
        options.General!.RecirculationDrainTag = "DRAIN";
        var gateway = new RecordingPlcGateway();
        using var supervisor = CreateSupervisor(options, gateway);
        await supervisor.StartRecirculationDrainAsync();
        Assert.True(supervisor.RecirculationDrainActive);
        Assert.Equal(("DRAIN", 1), Assert.Single(gateway.Writes));
        gateway.ReadValue = "1";
        await supervisor.PollRecirculationAsync(default);
        Assert.True(supervisor.RecirculationDrainActive);
        gateway.ReadValue = "0";
        await supervisor.PollRecirculationAsync(default);
        Assert.False(supervisor.RecirculationDrainActive);
        await supervisor.PollRecirculationAsync(default);
        Assert.Equal(2, gateway.ReadCount);
        Assert.Single(gateway.Writes);
        gateway.FailWrites = true;
        await Assert.ThrowsAsync<IOException>(() => supervisor.StartRecirculationDrainAsync());
        Assert.False(supervisor.RecirculationDrainActive);
    }

    [Fact]
    public async Task BlankRecirculationTagNeverReadsOrWrites()
    {
        var options = CreateOptions(1);
        options.General!.RecirculationDrainTag = " ";
        var gateway = new RecordingPlcGateway();
        using var supervisor = CreateSupervisor(options, gateway);
        await supervisor.PollRecirculationAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.StartRecirculationDrainAsync());
        Assert.Empty(gateway.Writes);
        Assert.Equal(0, gateway.ReadCount);
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
        public string ReadValue { get; set; } = "0";
        public string? FailReadTag { get; set; }
        public int ReadCount { get; private set; }
        public Task<string?> ReadTagAsync(string tag, CancellationToken token)
        {
            ReadCount++;
            if (tag == FailReadTag) throw new IOException("Read failed");
            return Task.FromResult<string?>(ReadValue);
        }
        public bool FailWrites { get; set; }
        public bool IsConnected { get; set; } = true;
        public bool ReadsHealthy => true;
        public event Action<string, string>? TagChanged;
        public System.Collections.Concurrent.ConcurrentQueue<(string Tag, int Value)> Writes { get; } = new();
        public void Emit(string tag, string value) => TagChanged?.Invoke(tag, value);
        public Task ConnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(true);
        public Task SendChuteAsync(string tag, int chute, int repeat, CancellationToken token)
        {
            if (FailWrites) throw new IOException("Write failed");
            Writes.Enqueue((tag, chute));
            return Task.CompletedTask;
        }
    }
}
