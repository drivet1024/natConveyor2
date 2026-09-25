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
    public void HistoryWindowIncludesTodayAndPreviousTwentyNineDays()
    {
        var (from, to) = StatisticsHistoryService.GetThirtyDayWindow(new DateTime(2026, 9, 24, 15, 30, 0));

        Assert.Equal(new DateTime(2026, 8, 26), from);
        Assert.Equal(new DateTime(2026, 9, 25), to);
        Assert.Equal(30, (to - from).TotalDays);
    }

    [Fact]
    public void DimensionHistoryUsesCombinedReadParcelsAsDenominator()
    {
        var rate = StatisticsHistoryService.CalculateDimensionErrorPercent([
            new() { TotalParcels = 100, NoReads = 20, DimensionErrors = 8 },
            new() { TotalParcels = 300, NoReads = 80, DimensionErrors = 22 }
        ]);

        Assert.Equal(10, rate);
    }

    [Fact]
    public void WeightAndScaleErrorsUseSeparateCountsAndDenominatorsForEveryDestination()
    {
        var counters = new LineCounters { TotalParcels = 100, NoReads = 20, ScaleErrors = 10, ScaleFaults = 3 };
        foreach (var destination in Enum.GetValues<StatisticsDestination>())
        {
            var row = CounterStatistics.Capture(28, 31, DateTime.Today, counters) with { Destination = destination };
            Assert.Equal(10, row.WeightErrors);
            Assert.Equal(12.5, row.WeightErrorPercent);
            Assert.Equal(3, row.ScaleErrors);
            Assert.Equal(3, row.ScaleErrorPercent);
            var restored = row.RestoreCounters();
            Assert.Equal(10, restored.ScaleErrors);
            Assert.Equal(3, restored.ScaleFaults);
        }
    }

    [Fact]
    public void GlobalErrorPercentagesUseCombinedCountsInsteadOfAveragingLines()
    {
        var row = CounterStatistics.CaptureCombined(28, DateTime.Today,
            [new() { TotalParcels = 100, NoReads = 20, ScaleErrors = 10, ScaleFaults = 3, LightParcels = 9, SmallParcels = 7, InverseLengthParcels = 4, SortedWithoutIssue = 70, Code98RecirculatedOverTwice = 2 },
             new() { TotalParcels = 300, NoReads = 80, ScaleErrors = 20, ScaleFaults = 5, LightParcels = 11, SmallParcels = 13, InverseLengthParcels = 6, SortedWithoutIssue = 210, Code98RecirculatedOverTwice = 3 }]);
        Assert.Equal(30, row.WeightErrors);
        Assert.Equal(10, row.WeightErrorPercent);
        Assert.Equal(8, row.ScaleErrors);
        Assert.Equal(2, row.ScaleErrorPercent);
        Assert.Equal(20, row.LightParcels);
        Assert.Equal(20, row.SmallParcels);
        Assert.Equal(10, row.InverseLengthParcels);
        Assert.Equal(280, row.Counters!.SortedWithoutIssue);
        Assert.Equal(5, row.Counters.Code98RecirculatedOverTwice);
    }

    [Fact]
    public void RejectionCausesSurviveCaptureAndCombineWithoutLosingLegacyCounts()
    {
        var first = new LineCounters { TotalParcels = 100, Rejected = 2,
            RejectedShipmentNotFound = 3, RejectedRouteNotConfigured = 4 };
        var second = new LineCounters { TotalParcels = 100, RejectedCode86RetryLimit = 5,
            RejectedProcessingError = 1 };

        var row = CounterStatistics.CaptureCombined(28, DateTime.Today, [first, second]);

        Assert.Equal(15, row.Rejected);
        Assert.Equal(7.5, row.RejectedPercent);
        Assert.Equal(3, row.RestoreCounters().RejectedShipmentNotFound);
        Assert.Equal(4, row.RestoreCounters().RejectedRouteNotConfigured);
        Assert.Equal(5, row.RestoreCounters().RejectedCode86RetryLimit);
        Assert.Equal(1, row.RestoreCounters().RejectedProcessingError);
        Assert.Equal(2, row.RestoreCounters().Rejected);
    }

    [Fact]
    public void ErrorPercentagesHandleNoParcelsOrNoReadParcels()
    {
        var empty = CounterStatistics.Capture(28, 31, DateTime.Today, new());
        Assert.Equal(0, empty.WeightErrorPercent);
        Assert.Equal(0, empty.ScaleErrorPercent);
        var unread = CounterStatistics.Capture(28, 31, DateTime.Today,
            new() { TotalParcels = 3, NoReads = 3, ScaleFaults = 1 });
        Assert.Equal(0, unread.WeightErrorPercent);
        Assert.Equal(33.33, unread.ScaleErrorPercent);
    }

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
        Assert.Equal(new int?[] { 31, 30 }, maintenance.Select(row => row.LineId));
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

    [Fact]
    public void MissingDatabaseLineIdIsCapturedAsNull()
    {
        using var fixture = new Fixture();
        fixture.Options.Lines[0].DatabaseLineId = null;
        var counters = new LineCounters { TotalParcels = 1 };
        var snapshot = new LineSnapshot(0, "Test", true, new(false, false, false, false, false), counters,
            null, null, DateTimeOffset.Now, ProductionCounters: counters);

        var rows = CounterStatisticsService.Capture(fixture.Options,
            new Dictionary<int, LineSnapshot> { [0] = snapshot }, DateTime.Today);

        Assert.Null(rows.Single(row => row.Destination == StatisticsDestination.ProductionLine).LineId);
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
    public void ShiftBoundaryBelongsToNewShiftAndLegacySaveTimeIsIgnored()
    {
        var options = new StatisticsOptions { Enabled = true, SaveTime = new(20, 0) };
        var boundary = new DateTime(2026, 9, 23, 20, 0, 0);
        Assert.Equal(boundary, options.GetShiftStart(boundary));
        Assert.Equal(boundary.AddDays(-1), options.GetShiftStart(boundary.AddTicks(-1)));
        Assert.Null(options.ValidationError([new() { DatabaseLineId = null }]));
        Assert.Null(options.ValidationError([new() { DatabaseLineId = 31 }]));
        Assert.NotNull(options.ValidationError([new() { DatabaseLineId = 31 }, new() { DatabaseLineId = 31 }]));
    }

    [Fact]
    public async Task LiveUpdatesReplaceSameShiftAndRestoreExactIndependentModes()
    {
        using var f = new Fixture();
        var service = await f.Start();
        f.Production = new() { TotalParcels = 100, Code98 = 7, NoReads = 11, ScaleFaults = 3, SortedByPostalCode = 57 };
        f.Maintenance = new() { TotalParcels = 9, ScaleErrors = 2 };
        await f.Tick(service);
        f.Production.TotalParcels = 120;
        await f.Tick(service, f.Now.AddSeconds(5));
        Assert.Equal(3, f.Store.Rows.Count);
        Assert.Equal(120, f.Store.Rows.Single(row => row.Destination == StatisticsDestination.ProductionLine).Scanned);
        f.Production = new(); f.Maintenance = new();
        await f.Start();
        Assert.Equal(120, f.Production.TotalParcels);
        Assert.Equal(7, f.Production.Code98);
        Assert.Equal(11, f.Production.NoReads);
        Assert.Equal(3, f.Production.ScaleFaults);
        Assert.Equal(57, f.Production.SortedByPostalCode);
        Assert.Equal(9, f.Maintenance.TotalParcels);
        Assert.Equal(2, f.Maintenance.ScaleErrors);
    }

    [Fact]
    public async Task MidnightDoesNotResetButShiftStartResetsBothModesOnce()
    {
        using var f = new Fixture();
        var service = await f.Start();
        f.Production.TotalParcels = 100;
        f.Maintenance.TotalParcels = 12;
        await f.Tick(service);
        await f.Tick(service, new(2026, 9, 23, 0, 0, 0));
        Assert.Equal(0, f.Resets);
        await f.Tick(service, new(2026, 9, 23, 20, 0, 0));
        Assert.Equal(1, f.Resets);
        Assert.Equal(0, f.Production.TotalParcels);
        Assert.Equal(0, f.Maintenance.TotalParcels);
        Assert.Equal(100, f.Store.Rows.Single(row => row.Destination == StatisticsDestination.ProductionLine && row.ShiftStartedAt.Day == 22).Scanned);
        f.Production.TotalParcels = 4;
        await f.Tick(service, new(2026, 9, 23, 20, 0, 5));
        Assert.Equal(1, f.Resets);
        Assert.Equal(4, f.Store.Rows.Single(row => row.Destination == StatisticsDestination.ProductionLine && row.ShiftStartedAt.Day == 23).Scanned);
    }

    [Fact]
    public async Task OfflineCapturesAreCoalescedAndTakePriorityAtRestart()
    {
        using var f = new Fixture();
        var service = await f.Start();
        f.Production.TotalParcels = 100;
        await f.Tick(service);
        f.Store.Fail = true;
        f.Production.TotalParcels = 110;
        await f.Tick(service, f.Now.AddSeconds(5));
        f.Production.TotalParcels = 130;
        await f.Tick(service, f.Now.AddSeconds(10));
        f.Store.Fail = false;
        f.Production = new();
        var restarted = await f.Start();
        Assert.Equal(130, f.Production.TotalParcels);
        await f.Tick(restarted, f.Now.AddSeconds(15));
        Assert.Equal(130, f.Store.Rows.Single(row => row.Destination == StatisticsDestination.ProductionLine).Scanned);
        Assert.Equal(2, f.Store.Rows.Count);
    }

    [Fact]
    public async Task OfflineShiftRolloverKeepsPreviousShiftPending()
    {
        using var f = new Fixture();
        var service = await f.Start();
        f.Production.TotalParcels = 91;
        f.Store.Fail = true;
        await f.Tick(service, new(2026, 9, 23, 20, 0, 0));
        Assert.Equal(0, f.Production.TotalParcels);
        f.Store.Fail = false;
        await f.Tick(service, new(2026, 9, 23, 20, 0, 5));
        Assert.Equal(91, f.Store.Rows.Single(row => row.Destination == StatisticsDestination.ProductionLine && row.ShiftStartedAt.Day == 22).Scanned);
        Assert.Equal(0, f.Store.Rows.Single(row => row.Destination == StatisticsDestination.ProductionLine && row.ShiftStartedAt.Day == 23).Scanned);
    }

    [Fact]
    public async Task FailedRestoreDoesNotApplyPartialCountersAndCanRetry()
    {
        using var f = new Fixture();
        var service = f.Create();
        f.Store.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => f.Initialize(service));
        Assert.False(service.Initialized);
        Assert.Equal(0, f.Restores);
        f.Store.Fail = false;
        await f.Initialize(service);
        Assert.True(service.Initialized);
        Assert.Equal(1, f.Restores);
    }

    [Fact]
    public async Task NewShiftStartupDoesNotRestorePreviousShift()
    {
        using var f = new Fixture();
        var service = await f.Start();
        f.Production.TotalParcels = 80;
        await f.Tick(service);
        f.Now = new(2026, 9, 23, 20, 0, 0);
        await f.Start();
        Assert.Equal(0, f.Production.TotalParcels);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledOrSimulatedDoesNotWrite(bool enabled, bool simulation)
    {
        using var f = new Fixture();
        f.Options.Statistics.Enabled = enabled;
        f.Options.Simulation = simulation;
        await f.Tick(f.Create());
        Assert.Empty(f.Store.Rows);
        Assert.False(Directory.Exists(Path.Combine(f.DirectoryPath, "data")));
    }

    [Fact]
    public async Task CorruptLocalStateBlocksRestoreWithoutOverwritingFile()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.DirectoryPath, "data"));
        var path = Path.Combine(f.DirectoryPath, "data", "counter-statistics.json");
        await File.WriteAllTextAsync(path, "invalid json");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => f.Start());
        Assert.Equal("invalid json", await File.ReadAllTextAsync(path));
        Assert.Equal(0, f.Restores);
    }

    [Fact]
    public async Task LegacyPendingCaptureIsRestoredBeforeFirstLiveUpdate()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.DirectoryPath, "data"));
        var legacy = new CounterStatistics(28, 31, new(2026, 9, 22, 20, 0, 0), 100, 7, 4, 80, 7, 4, 8, 2);
        await File.WriteAllTextAsync(Path.Combine(f.DirectoryPath, "data", "counter-statistics.json"),
            System.Text.Json.JsonSerializer.Serialize(new CounterStatisticsService.StatisticsState { Pending = [legacy] }));
        var service = await f.Start();
        Assert.Equal(100, f.Production.TotalParcels);
        Assert.Equal(8, f.Production.Code98);
        Assert.Equal(4, f.Production.Code97);
        await f.Tick(service);
        Assert.Equal(100, f.Store.Rows.Single(row => row.Destination == StatisticsDestination.ProductionLine).Scanned);
    }

    [Fact]
    public async Task FailedDiskCapturePreventsShiftReset()
    {
        using var f = new Fixture();
        var service = await f.Start();
        f.Production.TotalParcels = 37;
        Directory.CreateDirectory(Path.Combine(f.DirectoryPath, "data", "counter-statistics.json.tmp"));
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            try { await f.Tick(service, new(2026, 9, 23, 20, 0, 0)); }
            catch (UnauthorizedAccessException exception) { throw new IOException("Disk unavailable", exception); }
        });
        Assert.Equal(0, f.Resets);
        Assert.Equal(37, f.Production.TotalParcels);
        Assert.Empty(f.Store.Rows);
    }

    [Fact]
    public async Task ReadOnlyApplicationStateFallsBackAndRestoresPendingCounters()
    {
        using var f = new Fixture();
        var service = await f.Start();
        f.Store.Fail = true;
        f.Production.TotalParcels = 5;
        await f.Tick(service);
        var applicationState = Path.Combine(f.DirectoryPath, "data", "counter-statistics.json");
        File.SetAttributes(applicationState, FileAttributes.ReadOnly);
        try
        {
            f.Production.TotalParcels = 7;
            await f.Tick(service, f.Now.AddSeconds(5));
            Assert.True(Directory.Exists(f.FallbackDirectory));
            f.Store.Fail = false;
            f.Production = new();
            var restarted = f.Create();
            await f.Initialize(restarted);
            Assert.Equal(7, f.Production.TotalParcels);
        }
        finally { File.SetAttributes(applicationState, FileAttributes.Normal); }
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "conveyor-statistics-" + Guid.NewGuid());
        public string FallbackDirectory => Path.Combine(DirectoryPath, "fallback");
        public ConveyorOptions Options { get; } = new()
        {
            Simulation = false, General = new() { DepotId = 28 }, Statistics = new() { Enabled = true },
            LineCount = 1, Lines = [new() { Id = 0, DatabaseLineId = 31 }, new() { Id = 1, DatabaseLineId = 30 }]
        };
        public FakeStore Store { get; } = new();
        public DateTime Now { get; set; } = new(2026, 9, 22, 23, 0, 0);
        public LineCounters Production { get; set; } = new();
        public LineCounters Maintenance { get; set; } = new();
        public int Resets { get; private set; }
        public int Restores { get; private set; }
        public CounterStatisticsService Create() => new(Microsoft.Extensions.Options.Options.Create(Options), Store,
            new TestEnvironment { ContentRootPath = DirectoryPath }, NullLogger<CounterStatisticsService>.Instance)
            { FallbackDirectoryOverride = FallbackDirectory };
        public Task Initialize(CounterStatisticsService service) => service.InitializeAsync(Now,
            (_, production, maintenance) => { Production = production; Maintenance = maintenance; Restores++; }, CancellationToken.None);
        public async Task<CounterStatisticsService> Start()
        {
            var service = Create(); await Initialize(service); return service;
        }
        public Task Tick(CounterStatisticsService service, DateTime? now = null) => service.TickAsync(now ?? Now,
            () => [new(0, "Test", false, new(false, false, false, false, false), Production, null, null, DateTimeOffset.Now,
                ProductionCounters: Production.Copy(), MaintenanceCounters: Maintenance.Copy())], CancellationToken.None,
            () => { Production = new(); Maintenance = new(); Resets++; });
        public void Dispose() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); }
    }

    private sealed class FakeStore : ICounterStatisticsStore
    {
        public bool Fail { get; set; }
        public List<CounterStatistics> Rows { get; } = [];
        public Task<LineCounters> LoadAsync(int depotId, int? lineId, DateTime shift, StatisticsDestination destination, CancellationToken token)
        {
            if (Fail) throw new IOException("Database unavailable");
            return Task.FromResult(Rows.SingleOrDefault(row => row.DepotId == depotId && row.LineId == lineId
                && row.ShiftStartedAt == shift && row.Destination == destination)?.Counters?.Copy() ?? new());
        }
        public Task SaveAsync(CounterStatistics statistics, CancellationToken token)
        {
            if (Fail) throw new IOException("Database unavailable");
            Rows.RemoveAll(row => row.DepotId == statistics.DepotId && row.LineId == statistics.LineId
                && row.ShiftStartedAt == statistics.ShiftStartedAt && row.Destination == statistics.Destination);
            Rows.Add(statistics);
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
