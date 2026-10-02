using System.Reflection;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;
using Conveyor.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Conveyor.Web.Tests;

public class SortingDayArchiveTests
{
    [Fact]
    public async Task HistoryPageRendersOldDayCadenceAndUnknownLegacyFields()
    {
        var day = new DateTime(2025, 9, 1);
        var store = new MemoryStore();
        store.Saved["old"] = new()
        {
            ShiftStart = day.AddHours(10), Source = "legacy",
            Lines = [SortingDaySnapshot.FromLegacy(0, null, false, """{"TotalParcels":100,"NoReads":2}""")],
            Cadence = [new(new DateTimeOffset(day.AddHours(18)), 1234)]
        };
        using var services = new ServiceCollection().AddLogging().AddOptions()
            .AddSingleton<ISortingDayArchiveStore>(store).BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(services,
            services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Conveyor.Web.Components.Pages.SortingDayHistory>()).ToHtmlString());
        html = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("01/09/2025 10:00", html);
        Assert.Matches("Code 68</td><td[^>]*>—</td>", html);
        Assert.Contains("18:00", html);
        Assert.DoesNotContain("Aucun point de cadence sauvegardé", html);
        Assert.Contains("Totaux sauvegardés de la journée", html);
    }

    [Fact]
    public void LegacyMissingFieldsRemainUnknownAndPercentagesUseCorrectDenominator()
    {
        var line = SortingDaySnapshot.FromLegacy(0, null, false,
            """{"TotalParcels":100,"NoReads":20,"ScaleErrors":8,"SortedWithoutIssue":70}""");
        var metrics = SortingDaySnapshot.Metrics(line);
        Assert.Equal(20m, metrics["NoReadsPercent"]);
        Assert.Equal(10m, metrics["ScaleErrorsPercent"]);
        Assert.Equal(70m, metrics["SortedWithoutIssuePercent"]);
        Assert.Null(metrics["Code68"]);
        Assert.Null(metrics["Code68Percent"]);
        Assert.Null(metrics["ParcelsPerHour"]);
        Assert.Null(metrics["AverageWeightPounds"]);
    }

    [Fact]
    public void MetricsPreserveMeasurementsAndBothModes()
    {
        var metrics = SortingDaySnapshot.Metrics(new(0, null, true, new()
        {
            TotalParcels = 100, SortingRunSeconds = 600, MeasuredWeightPounds = 50,
            WeightMeasuredParcels = 20, MeasuredVolumeCubicFeet = 12, VolumeMeasuredParcels = 6,
            Code68 = 4, ChuteDispatchCounts = new() { [3] = 96 }
        }));
        Assert.Equal(600m, metrics["ParcelsPerHour"]);
        Assert.Equal(2.5m, metrics["AverageWeightPounds"]);
        Assert.Equal(2m, metrics["AverageVolumeCubicFeet"]);
        Assert.Equal(4m, metrics["Code68Percent"]);
        foreach (var field in SortingDaySnapshot.CounterFields) Assert.True(metrics.ContainsKey(field.Name));
    }

    [Fact]
    public void CaptureSeparatesResetsSessionsAndDaysAndCopiesCounters()
    {
        var config = new ConveyorOptions { Lines = [new() { Id = 0 }], Statistics = new() { ShiftStartTime = new(10, 0) } };
        config.ApplyGlobalSorting();
        var proxy = DispatchProxy.Create<IConveyorSupervisor, SupervisorProxy>();
        var state = (SupervisorProxy)(object)proxy;
        var at = new DateTimeOffset(new DateTime(2026, 10, 1, 23, 1, 0));
        var production = new LineCounters { TotalParcels = 120 };
        state.Lines = [new(0, "Ligne", true, new(true, true, true, true, true), new(), null, null, at,
            ProductionCounters: production, MaintenanceCounters: new() { TotalParcels = 3 })];
        var points = new[] { new CadencePoint(at.AddDays(-1), 999), new CadencePoint(at, 720) };
        var first = SortingDayArchiveService.Capture(config, proxy, points, at, "session", 0);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), first.ShiftStart);
        Assert.Equal(120, first.Lines.Single(l => !l.Maintenance).Counters.TotalParcels);
        Assert.Equal(3, first.Lines.Single(l => l.Maintenance).Counters.TotalParcels);
        Assert.Equal(720, Assert.Single(first.Cadence).ParcelsPerHour);
        Assert.True(Assert.Single(first.Chutes).Full);
        Assert.Equal(15, Assert.Single(first.Chutes).FullSeconds);
        production.TotalParcels = 130;
        Assert.Equal(120, first.Lines[0].Counters.TotalParcels);
        Assert.Equal(first.Id, SortingDayArchiveService.Capture(config, proxy, points, at.AddSeconds(5), "session", 0).Id);
        Assert.NotEqual(first.Id, SortingDayArchiveService.Capture(config, proxy, points, at, "session", 1).Id);
        Assert.NotEqual(first.Id, SortingDayArchiveService.Capture(config, proxy, points, at, "restart", 0).Id);
        Assert.NotEqual(first.Id, SortingDayArchiveService.Capture(config, proxy, points, at.AddMinutes(10), "session", 0).Id);
        Assert.Equal(first.ShiftStart, SortingDayArchiveService.Capture(config, proxy, points, at.AddHours(2), "session", 0).ShiftStart);
        Assert.Equal(first.ShiftStart.AddDays(1), SortingDayArchiveService.Capture(config, proxy, points, at.AddHours(11), "session", 0).ShiftStart);
    }

    [Fact]
    public void RepeatedClockHourHasDistinctUtcSlots()
    {
        var early = new DateTimeOffset(2026, 11, 1, 1, 15, 0, TimeSpan.FromHours(-4));
        var late = early.ToOffset(TimeSpan.FromHours(-5)).AddHours(1);
        Assert.NotEqual(SortingDaySnapshot.SlotFor(early), SortingDaySnapshot.SlotFor(late));
        Assert.Equal(SortingDaySnapshot.SlotFor(early), SortingDaySnapshot.SlotFor(early.AddMinutes(4)));
    }

    [Fact]
    public async Task DatabaseOutageAndRestartPreserveSlotsWithoutDuplicatingLatestValues()
    {
        var directory = Path.Combine(Path.GetTempPath(), "archive-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "queue.json");
        try
        {
            var store = new MemoryStore { Fail = true };
            var buffer = new SortingDayArchiveBuffer(path, store);
            await buffer.EnqueueAsync(new() { Id = "first", FullChutesCount = 1 });
            await buffer.EnqueueAsync(new() { Id = "first", FullChutesCount = 2 });
            await buffer.EnqueueAsync(new() { Id = "second", FullChutesCount = 3 });
            await Assert.ThrowsAsync<IOException>(() => buffer.FlushAsync());
            store.Fail = false;
            var restarted = new SortingDayArchiveBuffer(path, store);
            await restarted.FlushAsync();
            Assert.Equal(2, store.Saved.Count);
            Assert.Equal(2, store.Saved["first"].FullChutesCount);
            Assert.Equal(3, store.Saved["second"].FullChutesCount);
            Assert.Equal("[]", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class MemoryStore : ISortingDayArchiveStore
    {
        public bool Fail;
        public Dictionary<string, SortingDaySnapshot> Saved { get; } = [];
        public Task SaveAsync(SortingDaySnapshot snapshot, CancellationToken token)
        {
            if (Fail) throw new IOException("Database offline");
            Saved[snapshot.Id] = snapshot;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SortingDaySnapshot>> LoadAsync(DateTime day, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<SortingDaySnapshot>>(Saved.Values.ToArray());
    }

    public class SupervisorProxy : DispatchProxy
    {
        public IReadOnlyList<LineSnapshot> Lines = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "GetSnapshots" => Lines,
            "get_ConveyorRunning" => true,
            "get_FullChutesCount" => (int?)4,
            "get_Code42Count" => (int?)7,
            "get_CurrentShiftId" => (int?)1,
            "get_FullChuteTransitions" => new Dictionary<int, long?> { [4] = 2 },
            "get_FullChuteDurations" => new Dictionary<int, TimeSpan?> { [4] = TimeSpan.FromSeconds(15) },
            "get_FullChutesActive" => new[] { 4 },
            _ => throw new NotSupportedException(method.Name)
        };
    }
}
