using Conveyor.Web.Domain;
using Conveyor.Web.Services;

namespace Conveyor.Web.Tests;

public class HistoryCadenceTests
{
    [Fact]
    public void TotalAddsLineRatesUsingEachLinesRuntime()
    {
        Assert.Equal(800d, StatisticsHistoryService.CalculateTotalCadence([
            new LineCounters { TotalParcels = 1200, SortingRunSeconds = 7200 },
            new LineCounters { TotalParcels = 200, SortingRunSeconds = 3600 },
            new LineCounters()
        ]));
    }

    [Fact]
    public void MissingRuntimeDoesNotProduceAPartialOrZeroRate()
    {
        Assert.Null(StatisticsHistoryService.CalculateTotalCadence([
            new LineCounters { TotalParcels = 1200, SortingRunSeconds = 7200 },
            new LineCounters { TotalParcels = 200 }
        ]));
        Assert.Null(StatisticsHistoryService.CalculateTotalCadence([]));
    }
}
