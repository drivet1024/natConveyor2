using System.Text.Json;
using Conveyor.Web.Domain;
using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class SortingTimeTests
{
    [Fact]
    public void RunningTimeExcludesStopsAndResetStartsFresh()
    {
        var repo = new SimulationConveyorRepository();
        var controller = new LineController(new LineOptions(), true, repo,
            new SimulationPlcGateway(NullLogger<SimulationPlcGateway>.Instance), false,
            new SortEngine(repo, NullLogger<SortEngine>.Instance), NullLogger.Instance, () => { });
        var start = DateTimeOffset.UtcNow.AddHours(-4);
        controller.RestoreCounters(new LineCounters { TotalParcels = 1200 }, new());
        controller.SetConveyorRunning(true, start);
        controller.SetConveyorRunning(false, start.AddHours(1));
        Assert.Equal(TimeSpan.FromHours(1), controller.Snapshot().Counters.SortingDuration);
        Assert.Equal(1200d, controller.Snapshot().Counters.ParcelsPerHour);
        controller.SetConveyorRunning(true, start.AddHours(2));
        controller.SetConveyorRunning(false, start.AddHours(3));
        var counters = controller.Snapshot().Counters;
        Assert.Equal(TimeSpan.FromHours(2), counters.SortingDuration);
        Assert.Equal(600d, counters.ParcelsPerHour);
        var restored = JsonSerializer.Deserialize<LineCounters>(JsonSerializer.Serialize(counters))!;
        Assert.Equal(counters.SortingDuration, restored.SortingDuration);
        controller.ResetCounters();
        Assert.Equal(TimeSpan.Zero, controller.Snapshot().Counters.SortingDuration);
        Assert.Equal(0d, controller.Snapshot().Counters.ParcelsPerHour);
        controller.SetConveyorRunning(true, start);
        controller.ResetCounters();
        controller.SetConveyorRunning(false);
        Assert.InRange(controller.Snapshot().Counters.SortingRunSeconds, 0, 2);
    }

    [Fact]
    public void LegacyCountersStartWithZeroDurationInsteadOfUnavailable()
    {
        var counters = JsonSerializer.Deserialize<LineCounters>("{\"TotalParcels\":1200,\"SortingTimingIncomplete\":true}")!;
        Assert.Equal(TimeSpan.Zero, counters.SortingDuration);
        Assert.Equal(0d, counters.ParcelsPerHour);
    }
}
