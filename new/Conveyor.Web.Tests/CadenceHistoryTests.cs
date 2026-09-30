using Conveyor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public class CadenceHistoryTests
{
    [Fact]
    public void MeasuresNewParcelsAcrossBothLinesOverTenMinutes()
    {
        using var history = new CadenceHistoryService(null!, NullLogger<CadenceHistoryService>.Instance);
        var now = DateTimeOffset.UtcNow;
        history.Observe(now, new() { [0] = 10000, [1] = 20000 });
        history.Observe(now.AddMinutes(5), new() { [0] = 10100, [1] = 20200 });
        Assert.Empty(history.GetPoints());
        history.Observe(now.AddMinutes(10), new() { [0] = 10200, [1] = 20400 });
        Assert.Equal(3600, Assert.Single(history.GetPoints()).ParcelsPerHour);
        history.Observe(now.AddMinutes(20), new() { [0] = 10200, [1] = 20400 });
        Assert.Equal(0, history.GetPoints()[1].ParcelsPerHour);
    }

    [Fact]
    public void CounterResetDiscardsPartialIntervalAndOldPointsExpire()
    {
        using var history = new CadenceHistoryService(null!, NullLogger<CadenceHistoryService>.Instance);
        var now = DateTimeOffset.UtcNow;
        history.Observe(now, new() { [0] = 100 });
        history.Observe(now.AddMinutes(5), new() { [0] = 150 });
        history.Observe(now.AddMinutes(6), new() { [0] = 0 });
        history.Observe(now.AddMinutes(10), new() { [0] = 100 });
        Assert.Empty(history.GetPoints());
        history.Observe(now.AddMinutes(16), new() { [0] = 500 });
        Assert.Equal(3000, Assert.Single(history.GetPoints()).ParcelsPerHour);
        history.Observe(now.AddHours(25), new() { [0] = 0 });
        Assert.Empty(history.GetPoints());
    }
}
