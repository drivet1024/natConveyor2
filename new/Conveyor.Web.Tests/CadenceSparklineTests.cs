using Microsoft.Extensions.DependencyInjection;
using Conveyor.Web.Components.Layout;

namespace Conveyor.Web.Tests;

public sealed class CadenceSparklineTests
{
    [Theory]
    [InlineData(0, "#ff6c79")]
    [InlineData(2500, "#ffd15c")]
    [InlineData(3500, "#50d98b")]
    public async Task FirstCompletedIntervalIsVisibleWithoutASecondPoint(double rate, string color)
    {
        using var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddLogging().AddOptions().BuildServiceProvider();
        await using var renderer = new Microsoft.AspNetCore.Components.Web.HtmlRenderer(services,
            services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>());
        var at = new DateTimeOffset(CadenceSparkline.WindowStart(DateTime.Now).AddMinutes(10));
        using var history = new Conveyor.Web.Services.CadenceHistoryService(null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Conveyor.Web.Services.CadenceHistoryService>.Instance);
        history.Observe(at.AddMinutes(-10), new() { [0] = 1000 });
        history.Observe(at, new() { [0] = 1000 + (long)Math.Round(rate / 6) });
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<CadenceSparkline>(
                Microsoft.AspNetCore.Components.ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(CadenceSparkline.Points)] = history.GetPoints()
                }))).ToHtmlString());
        var marker = System.Text.RegularExpressions.Regex.Match(html, "<circle\\b[^>]*>").Value;
        Assert.NotEmpty(marker);
        Assert.Contains($"fill=\"{color}\"", marker);
        Assert.Contains("Dernier relevé", System.Net.WebUtility.HtmlDecode(html));
        Assert.Contains("16:10", html);
        Assert.DoesNotContain("Aucune cadence disponible", html);
    }

    [Fact]
    public void GraphDoesNotConnectPointsAcrossMissingIntervals()
    {
        var now = DateTimeOffset.UtcNow;
        var segments = CadenceSparkline.Segments([
            new(now, 3000), new(now.AddMinutes(10), 2000),
            new(now.AddMinutes(40), 2500)]).ToArray();
        Assert.Equal(2, segments.Length);
        Assert.Equal(2, segments[0].Count);
        Assert.Single(segments[1]);
    }

    [Theory]
    [InlineData(0, 28)]
    [InlineData(3, 28)]
    [InlineData(15, 28)]
    [InlineData(16, 29)]
    [InlineData(23, 29)]
    public void WindowKeepsTheSameEveningAcrossMidnight(int hour, int startDay)
    {
        Assert.Equal(new DateTime(2026, 9, startDay, 16, 0, 0),
            CadenceSparkline.WindowStart(new DateTime(2026, 9, 29, hour, 0, 0)));
    }

    [Theory]
    [InlineData(0, 38)]
    [InlineData(5.5, 254)]
    [InlineData(11, 470)]
    public void PointsUseFixedElevenHourScale(double elapsedHours, double expectedX)
    {
        var start = new DateTime(2026, 9, 29, 16, 0, 0);
        Assert.Equal(expectedX, CadenceSparkline.Position(start.AddHours(elapsedHours), start));
    }
}
