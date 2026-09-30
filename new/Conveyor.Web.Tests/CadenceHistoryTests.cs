using Conveyor.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public class CadenceHistoryTests
{
    [Fact]
    public void RestorationDoesNotCountSavedParcelsAsNewTraffic()
    {
        using var history = new CadenceHistoryService(null!, NullLogger<CadenceHistoryService>.Instance);
        var now = DateTimeOffset.UtcNow;
        history.Observe(now, new() { [0] = 0 }, countersReady: false);
        history.Observe(now.AddMinutes(2), new() { [0] = 4404 }, countersReady: false);
        history.Observe(now.AddMinutes(3), new() { [0] = 4404 });
        history.Observe(now.AddMinutes(10), new() { [0] = 4604 });
        Assert.Empty(history.GetPoints());
        history.Observe(now.AddMinutes(13), new() { [0] = 4904 });
        Assert.Equal(3000, Assert.Single(history.GetPoints()).ParcelsPerHour);
    }

    [Fact]
    public async Task RestartLoadsCompletedPointsAndStartsFreshInterval()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cadence-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.json");
        try
        {
            var now = DateTimeOffset.UtcNow;
            using (var history = new CadenceHistoryService(null!, NullLogger<CadenceHistoryService>.Instance))
            {
                history.Observe(now, new() { [0] = 4000 });
                history.Observe(now.AddMinutes(10), new() { [0] = 4500 });
                await history.SaveAsync(path);
                history.Observe(now.AddMinutes(15), new() { [0] = 4800 });
            }
            using var restarted = new CadenceHistoryService(null!, NullLogger<CadenceHistoryService>.Instance);
            await restarted.LoadAsync(path, now.AddMinutes(30));
            Assert.Equal(3000, Assert.Single(restarted.GetPoints()).ParcelsPerHour);
            restarted.Observe(now.AddMinutes(30), new() { [0] = 5000 });
            restarted.Observe(now.AddMinutes(40), new() { [0] = 5200 });
            Assert.Equal(1200, restarted.GetPoints()[1].ParcelsPerHour);
            await restarted.SaveAsync(path);
            using var loaded = new CadenceHistoryService(null!, NullLogger<CadenceHistoryService>.Instance);
            await loaded.LoadAsync(path, now.AddMinutes(40));
            Assert.Equal(restarted.GetPoints().ToArray(), loaded.GetPoints().ToArray());
            await loaded.LoadAsync(path, now.AddHours(25));
            Assert.Empty(loaded.GetPoints());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FailedSaveCanBeRetriedWithoutLosingPoints()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cadence-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var blocked = Path.Combine(directory, "blocked");
            await File.WriteAllTextAsync(blocked, "file prevents directory creation");
            using var history = new CadenceHistoryService(null!, NullLogger<CadenceHistoryService>.Instance);
            var now = DateTimeOffset.UtcNow;
            history.Observe(now, new() { [0] = 0 });
            history.Observe(now.AddMinutes(10), new() { [0] = 500 });
            await Assert.ThrowsAnyAsync<IOException>(() => history.SaveAsync(Path.Combine(blocked, "history.json")));
            var path = Path.Combine(directory, "history.json");
            await history.SaveAsync(path);
            using var restored = new CadenceHistoryService(null!, NullLogger<CadenceHistoryService>.Instance);
            await restored.LoadAsync(path, now.AddMinutes(10));
            Assert.Equal(3000, Assert.Single(restored.GetPoints()).ParcelsPerHour);
        }
        finally { Directory.Delete(directory, true); }
    }

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
