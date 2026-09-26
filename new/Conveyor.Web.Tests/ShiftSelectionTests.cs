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

    private static ConveyorSupervisor CreateSupervisor(ConveyorOptions options)
    {
        var repository = new SimulationConveyorRepository();
        return new ConveyorSupervisor(Microsoft.Extensions.Options.Options.Create(options), repository,
            new SortEngine(repository, NullLogger<SortEngine>.Instance), NullLoggerFactory.Instance, new TestConfigurationEditor());
    }
}
