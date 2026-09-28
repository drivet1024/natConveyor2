using System.Text.Json;
using Conveyor.Web.Domain;

namespace Conveyor.Web.Tests;

public sealed class SortingTimeTests
{
    [Fact]
    public void DurationAndRateUseFullElapsedTimeAndSurviveRestart()
    {
        var start = DateTimeOffset.Parse("2026-09-28T08:00:00-04:00");
        var counters = new LineCounters();
        counters.RecordSortingTime(start);
        counters.TotalParcels++;
        Assert.Equal(TimeSpan.Zero, counters.SortingDuration);
        Assert.Null(counters.ParcelsPerHour);
        counters.RecordSortingTime(start.AddHours(2));
        counters.TotalParcels = 1200;
        Assert.Equal(TimeSpan.FromHours(2), counters.SortingDuration);
        Assert.Equal(600d, counters.ParcelsPerHour);

        var restored = JsonSerializer.Deserialize<LineCounters>(JsonSerializer.Serialize(counters))!;
        restored.RecordSortingTime(start.AddHours(3));
        restored.TotalParcels = 1800;
        Assert.Equal(TimeSpan.FromHours(3), restored.SortingDuration);
        Assert.Equal(600d, restored.ParcelsPerHour);
        Assert.Equal(TimeSpan.FromHours(2), counters.SortingDuration);
    }

    [Fact]
    public void LegacyCountsHaveNoInventedDurationOrRate()
    {
        var counters = JsonSerializer.Deserialize<LineCounters>("{\"TotalParcels\":1200}")!;
        Assert.Null(counters.SortingDuration);
        counters.RecordSortingTime(DateTimeOffset.UtcNow);
        counters.TotalParcels++;
        counters.RecordSortingTime(DateTimeOffset.UtcNow.AddHours(1));
        Assert.Null(counters.SortingDuration);
        Assert.Null(counters.ParcelsPerHour);
        Assert.Equal(TimeSpan.Zero, new LineCounters().SortingDuration);
    }
}
