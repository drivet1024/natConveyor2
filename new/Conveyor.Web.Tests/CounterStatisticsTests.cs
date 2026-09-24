using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class CounterStatisticsTests
{
    [Fact]
    public void GlobalProductionAndMaintenanceUseIndependentCountsAndWeightedPercentages()
    {
        using var fixture = new Fixture();
        fixture.Options.LineCount = 2;
        var production1 = new LineCounters { TotalParcels = 100, Rejected = 20, Code98 = 4 };
        var production2 = new LineCounters { TotalParcels = 300, Rejected = 0, Code98 = 8 };
        var maintenance1 = new LineCounters { TotalParcels = 10, Rejected = 5 };
        var maintenance2 = new LineCounters { TotalParcels = 40, Rejected = 5 };
        LineSnapshot Snapshot(int id, LineCounters production, LineCounters maintenance) => new(id, "Test", true,
            new(false, false, false, false, false), maintenance, null, null, DateTimeOffset.Now,
            Maintenance: true, ProductionCounters: production, MaintenanceCounters: maintenance);
        var rows = CounterStatisticsService.Capture(fixture.Options, new Dictionary<int, LineSnapshot>
        {
            [0] = Snapshot(0, production1, maintenance1), [1] = Snapshot(1, production2, maintenance2)
        }, new DateTime(2026, 9, 22, 20, 0, 0));
        Assert.Equal(5, rows.Length);
        Assert.Equal(new long[] { 100, 300 }, rows.Where(row => row.Destination == StatisticsDestination.ProductionLine).Select(row => row.Scanned));
        var global = Assert.Single(rows, row => row.Destination == StatisticsDestination.ProductionGlobal);
        Assert.Equal(400, global.Scanned);
        Assert.Equal(5, global.RejectedPercent);
        Assert.Equal(3, global.Code98Percent);
        var maintenance = rows.Where(row => row.Destination == StatisticsDestination.Maintenance).ToArray();
        Assert.Equal(2, maintenance.Length);
        Assert.Equal(new[] { 31, 30 }, maintenance.Select(row => row.LineId));
        Assert.Equal(new long[] { 10, 40 }, maintenance.Select(row => row.Scanned));
        Assert.Equal(new double[] { 50, 12.5 }, maintenance.Select(row => row.RejectedPercent));
        Assert.Equal(("conveyor_stats_dde_global", false), CounterStatisticsStore.GetDestination(global.Destination));
        Assert.Equal(("conveyor_stats_dde_maintenance", true), CounterStatisticsStore.GetDestination(maintenance[0].Destination));
    }

    [Fact]
    public void MaintenanceOnlyDoesNotProduceAnyProductionRows()
    {
        using var fixture = new Fixture();
        var counters = new LineCounters { TotalParcels = 12 };
        var snapshot = new LineSnapshot(0, "Test", true, new(false, false, false, false, false), counters,
            null, null, DateTimeOffset.Now, Maintenance: true, ProductionCounters: new(), MaintenanceCounters: counters);
        var rows = CounterStatisticsService.Capture(fixture.Options, new Dictionary<int, LineSnapshot> { [0] = snapshot }, DateTime.Today);
        var row = Assert.Single(rows);
        Assert.Equal(StatisticsDestination.Maintenance, row.Destination);
        Assert.Equal(31, row.LineId);
        Assert.Equal(12, row.Scanned);
    }

    [Fact]
    public void CountsAndPercentagesBelongToTheConfiguredDatabaseLine()
    {
        var start = new DateTime(2026, 9, 22, 20, 0, 0);
        var statistics = CounterStatistics.Capture(28, 31, start,
            new() { TotalParcels = 100, Rejected = 10, Code97 = 5, Code98 = 4, Code68 = 2, SortedByWaybill = 60, SortedByPostalCode = 20 });
        Assert.Equal(28, statistics.DepotId);
        Assert.Equal(31, statistics.LineId);
        Assert.Equal(start, statistics.ShiftStartedAt);
        Assert.Equal(100, statistics.Scanned);
        Assert.Equal(10, statistics.Rejected);
        Assert.Equal(5, statistics.Recycled);
        Assert.Equal(80, statistics.Sorted);
        Assert.Equal(10, statistics.RejectedPercent);
        Assert.Equal(5, statistics.RecycledPercent);
        Assert.Equal(4, statistics.Code98Percent);
        Assert.Equal(2, statistics.Code68Percent);
        var empty = CounterStatistics.Capture(28, 31, start, new());
        Assert.Equal(0, empty.RejectedPercent);
        Assert.Equal(0, empty.RecycledPercent);
        Assert.Equal(0, empty.Code98Percent);
        Assert.Equal(0, empty.Code68Percent);
    }

    [Theory]
    [InlineData(20, 0, 8, 20, 22)]
    [InlineData(6, 0, 15, 0, 23)]
    public void ShiftDateUsesTheBeginningOfTheShift(int startHour, int startMinute, int saveHour, int saveMinute, int expectedDay)
    {
        var options = new StatisticsOptions { ShiftStartTime = new(startHour, startMinute) };
        Assert.Equal(new DateTime(2026, 9, expectedDay, startHour, startMinute, 0),
            options.GetShiftStart(new DateTime(2026, 9, 23, saveHour, saveMinute, 0)));
    }

    [Fact]
    public void RejectsSavingAfterDailyResetButAllowsTheSameTime()
    {
        var options = new StatisticsOptions { Enabled = true };
        var lines = new[] { new LineOptions { DatabaseLineId = 31, EndOfDay = new(8, 25) } };
        Assert.Null(options.ValidationError(lines));
        options.SaveTime = new(8, 25);
        Assert.Null(options.ValidationError(lines));
        options.SaveTime = new(8, 26);
        Assert.NotNull(options.ValidationError(lines));
        options.SaveTime = options.ShiftStartTime;
        Assert.NotNull(options.ValidationError(lines));
    }

    [Fact]
    public async Task CapturesOnceAtScheduleAndDoesNotDuplicateAfterRestart()
    {
        using var fixture = new Fixture();
        var service = fixture.Create();
        await fixture.Tick(service, new(2026, 9, 23, 8, 19, 0));
        Assert.Empty(fixture.Store.Saved);
        await fixture.Tick(service, new(2026, 9, 23, 8, 20, 0));
        await fixture.Tick(service, new(2026, 9, 23, 8, 20, 5));
        await fixture.Tick(fixture.Create(), new(2026, 9, 23, 8, 21, 0));
        var statistics = Assert.Single(fixture.Store.Saved, row => row.Destination == StatisticsDestination.ProductionLine);
        Assert.Equal(new DateTime(2026, 9, 22, 20, 0, 0), statistics.ShiftStartedAt);
        Assert.Equal(100, statistics.Scanned); // The unconfigured second line is excluded.
    }

    [Fact]
    public async Task TwoLinesAreSavedSeparatelyAndPartialFailureResumesOnlyPendingLine()
    {
        using var fixture = new Fixture();
        fixture.Options.LineCount = 2;
        fixture.Store.FailLineId = 30;
        var service = fixture.Create();
        await fixture.Tick(service, new(2026, 9, 23, 8, 19, 0));
        await fixture.Tick(service, new(2026, 9, 23, 8, 20, 0));
        var first = Assert.Single(fixture.Store.Saved, row => row.Destination == StatisticsDestination.ProductionLine);
        Assert.Equal(31, first.LineId);
        Assert.Equal(100, first.Scanned);
        fixture.Store.FailLineId = null;
        fixture.Options.Lines[1].DatabaseLineId = 99;
        await fixture.Tick(fixture.Create(), new(2026, 9, 23, 9, 0, 0));
        Assert.Equal(3, fixture.Store.Saved.Count);
        var second = fixture.Store.Saved[1];
        Assert.Equal(30, second.LineId); // Keep the captured identity after a configuration change.
        Assert.Equal(999, second.Scanned);
        Assert.Equal(first.ShiftStartedAt, second.ShiftStartedAt);
        Assert.Equal(first.DepotId, second.DepotId);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 30)]
    [InlineData(31, 31)]
    public void StatisticsRequireDistinctPositiveDatabaseLineIds(int? first, int second)
    {
        var options = new StatisticsOptions { Enabled = true };
        Assert.NotNull(options.ValidationError([new() { DatabaseLineId = first }, new() { DatabaseLineId = second }]));
        options.Enabled = false;
        Assert.Null(options.ValidationError([new() { DatabaseLineId = first }, new() { DatabaseLineId = second }]));
    }

    [Fact]
    public async Task ResetRunsAfterDurableCaptureButBeforeDatabaseWrite()
    {
        using var fixture = new Fixture();
        var service = fixture.Create();
        await fixture.Tick(service, new(2026, 9, 23, 8, 19, 0));
        var reset = false;
        await fixture.Tick(service, new(2026, 9, 23, 8, 20, 0), () =>
        {
            Assert.Empty(fixture.Store.Saved);
            var state = System.Text.Json.JsonSerializer.Deserialize<CounterStatisticsService.StatisticsState>(
                File.ReadAllText(Path.Combine(fixture.DirectoryPath, "data", "counter-statistics.json")))!;
            Assert.Equal(100, Assert.Single(state.Pending, row => row.Destination == StatisticsDestination.ProductionLine).Scanned);
            fixture.Scanned = 0;
            reset = true;
        });
        Assert.True(reset);
        Assert.Equal(100, Assert.Single(fixture.Store.Saved, row => row.Destination == StatisticsDestination.ProductionLine).Scanned);
    }

    [Fact]
    public async Task DatabaseFailureKeepsOriginalSnapshotAcrossCounterResetAndRestart()
    {
        using var fixture = new Fixture();
        var service = fixture.Create();
        fixture.Store.Fail = true;
        await fixture.Tick(service, new(2026, 9, 23, 8, 19, 0));
        await fixture.Tick(service, new(2026, 9, 23, 8, 20, 0));
        fixture.Scanned = 0;
        await fixture.Tick(service, new(2026, 9, 23, 8, 20, 5));
        Assert.Equal(1, fixture.Store.Attempts);
        fixture.Store.Fail = false;
        await fixture.Tick(fixture.Create(), new(2026, 9, 23, 9, 0, 0));
        Assert.Equal(100, Assert.Single(fixture.Store.Saved, row => row.Destination == StatisticsDestination.ProductionLine).Scanned);
    }

    [Fact]
    public async Task LateStartupDoesNotInventMissedStatistics()
    {
        using var fixture = new Fixture();
        var service = fixture.Create();
        await fixture.Tick(service, new(2026, 9, 23, 8, 21, 0));
        Assert.Empty(fixture.Store.Saved);
        await fixture.Tick(service, new(2026, 9, 24, 8, 20, 0));
        Assert.Single(fixture.Store.Saved, row => row.Destination == StatisticsDestination.ProductionLine);
    }

    [Fact]
    public async Task DelayedTickAcrossMidnightKeepsCorrectShiftDate()
    {
        using var fixture = new Fixture();
        fixture.Options.Statistics.SaveTime = new(23, 59);
        var service = fixture.Create();
        await fixture.Tick(service, new(2026, 9, 23, 23, 58, 0));
        await fixture.Tick(service, new(2026, 9, 24, 0, 0, 5));
        Assert.Equal(new DateTime(2026, 9, 23, 20, 0, 0), Assert.Single(fixture.Store.Saved, row => row.Destination == StatisticsDestination.ProductionLine).ShiftStartedAt);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledOrSimulatedDoesNotWrite(bool enabled, bool simulation)
    {
        using var fixture = new Fixture();
        fixture.Options.Simulation = simulation;
        fixture.Options.Statistics.Enabled = enabled;
        var service = fixture.Create();
        await fixture.Tick(service, new(2026, 9, 23, 8, 19, 0));
        await fixture.Tick(service, new(2026, 9, 23, 8, 20, 0));
        Assert.Empty(fixture.Store.Saved);
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoryPath, "data")));
    }

    [Fact]
    public async Task CorruptStateIsNotOverwrittenAndPreventsResetByFailingTheTick()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "data"));
        var path = Path.Combine(fixture.DirectoryPath, "data", "counter-statistics.json");
        await File.WriteAllTextAsync(path, "invalid json");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => fixture.Tick(fixture.Create(), new(2026, 9, 23, 8, 20, 0)));
        Assert.Equal("invalid json", await File.ReadAllTextAsync(path));
        Assert.Empty(fixture.Store.Saved);
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "conveyor-statistics-" + Guid.NewGuid());
        public ConveyorOptions Options { get; } = new()
        {
            Simulation = false, General = new() { DepotId = 28 }, Statistics = new() { Enabled = true, SaveTime = new(8, 20) },
            LineCount = 1, Lines = [new() { Id = 0, DatabaseLineId = 31 }, new() { Id = 1, DatabaseLineId = 30 }]
        };
        public FakeStore Store { get; } = new();
        public long Scanned { get; set; } = 100;
        public CounterStatisticsService Create() => new(Microsoft.Extensions.Options.Options.Create(Options), Store,
            new TestEnvironment { ContentRootPath = DirectoryPath }, NullLogger<CounterStatisticsService>.Instance);
        public Task Tick(CounterStatisticsService service, DateTime now, Action? afterCapture = null) => service.TickAsync(now,
            () => [Snapshot(0, Scanned), Snapshot(1, 999)], CancellationToken.None, afterCapture);
        private static LineSnapshot Snapshot(int id, long scanned) => new(id, "Test", true,
            new(false, false, false, false, false), new() { TotalParcels = scanned }, null, null, DateTimeOffset.Now);
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }

    private sealed class FakeStore : ICounterStatisticsStore
    {
        public bool Fail { get; set; }
        public int? FailLineId { get; set; }
        public int Attempts { get; private set; }
        public List<CounterStatistics> Saved { get; } = [];
        public Task SaveAsync(CounterStatistics statistics, CancellationToken token)
        {
            Attempts++;
            if (Fail || statistics.LineId == FailLineId) throw new IOException("Database unavailable");
            Saved.Add(statistics);
            return Task.CompletedTask;
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
