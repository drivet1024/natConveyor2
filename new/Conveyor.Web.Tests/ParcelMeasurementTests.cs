using Conveyor.Web.Components.Layout;
using Conveyor.Web.Domain;
using Conveyor.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace Conveyor.Web.Tests;

public class ParcelMeasurementTests
{
    [Fact]
    public void ConvertsInchesToCubicFeetAndExcludesInvalidMeasurementsIndependently()
    {
        var counters = new LineCounters { TotalParcels = 4 };
        counters.RecordMeasurements(new(12, 12, 12), 10, 100, 150);
        counters.RecordMeasurements(new(6, 12, 12), 20, 100, 150);
        counters.RecordMeasurements(Dimension.Missing, 5, 100, 150);
        counters.RecordMeasurements(new(101, 1, 1), -1, 100, 150);
        Assert.Equal(1.5m, counters.MeasuredVolumeCubicFeet);
        Assert.Equal(2, counters.VolumeMeasuredParcels);
        Assert.Equal(35m, counters.MeasuredWeightPounds);
        Assert.Equal(3, counters.WeightMeasuredParcels);
    }

    [Fact]
    public void CombiningAndRestoringPreservesWeightedMeansAndMissingCoverage()
    {
        var first = new LineCounters { TotalParcels = 1 };
        first.RecordMeasurements(new(12, 12, 12), 10, 100, 150);
        var second = new LineCounters { TotalParcels = 2 };
        second.RecordMeasurements(new(24, 12, 12), 20, 100, 150);
        second.RecordMeasurements(new(24, 12, 12), 20, 100, 150);
        var legacy = JsonSerializer.Deserialize<LineCounters>("{\"TotalParcels\":10}")!;
        var combined = CounterStatistics.CaptureCombined(1, DateTime.Today, [first, second, legacy]);
        var saved = JsonSerializer.Deserialize<CounterStatistics>(JsonSerializer.Serialize(combined))!;
        var restored = saved.RestoreCounters();
        Assert.Equal(13, restored.TotalParcels);
        Assert.Equal(3, restored.VolumeMeasuredParcels);
        Assert.Equal(5m / 3, restored.MeasuredVolumeCubicFeet / restored.VolumeMeasuredParcels);
        Assert.Equal(50m / 3, restored.MeasuredWeightPounds / restored.WeightMeasuredParcels);
        var point = new StatisticsHistoryPoint(DateTime.Today, 13, 0, 0, null, null)
        { TotalVolumeCubicFeet = restored.MeasuredVolumeCubicFeet, VolumeMeasuredParcels = restored.VolumeMeasuredParcels };
        Assert.Equal(5m / 3, point.AverageVolumeCubicFeet);
        restored.RecordMeasurements(new(12, 12, 12), 10, 100, 150);
        Assert.Equal(6m, restored.MeasuredVolumeCubicFeet);
        Assert.Equal(4, restored.VolumeMeasuredParcels);
    }

    [Fact]
    public void OldCountersNeverManufactureZeroVolumesOrWeights()
    {
        var legacy = JsonSerializer.Deserialize<LineCounters>("{\"TotalParcels\":100}")!;
        var combined = CounterStatistics.CaptureCombined(1, DateTime.Today, [legacy]).RestoreCounters();
        Assert.Null(combined.MeasuredVolumeCubicFeet);
        Assert.Null(combined.MeasuredWeightPounds);
        Assert.Equal(0, combined.VolumeMeasuredParcels);
        var point = new StatisticsHistoryPoint(DateTime.Today, 100, 0, 0, 5, 2);
        Assert.Null(point.AverageVolumeCubicFeet);
        Assert.Null(point.AverageWeightPounds);
    }

    [Fact]
    public async Task ChartUsesTwoUnitAxesAndDoesNotPlotEmptyOrUnknownShifts()
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var points = new[] {
            new StatisticsHistoryPoint(DateTime.Today.AddDays(-2), 100, 0, 0, 10, 2),
            new StatisticsHistoryPoint(DateTime.Today.AddDays(-1), 0, 0, 0, 0, 0),
            new StatisticsHistoryPoint(DateTime.Today, 4, 0, 0, 2, 1) {
                TotalVolumeCubicFeet = 1.5m, VolumeMeasuredParcels = 2,
                AverageWeightPounds = 10m, WeightMeasuredParcels = 3 }
        };
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ParcelCategoryChart>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(ParcelCategoryChart.Points)] = points }))).ToHtmlString());
        html = System.Net.WebUtility.HtmlDecode(html);
        Assert.Contains("0,75 pi³", html);
        Assert.Contains("10,00 lb", html);
        Assert.Contains("VOLUME · PI³", html);
        Assert.Contains("POIDS · LB", html);
        Assert.Contains("2 / 4", html);
        Assert.Contains("3 / 4", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "<circle\\b").Count);
        Assert.DoesNotContain("Petits colis", html);
    }
}
