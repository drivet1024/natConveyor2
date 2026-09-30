using Conveyor.Web.Components.Layout;

namespace Conveyor.Web.Tests;

public sealed class CadenceSparklineTests
{
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
