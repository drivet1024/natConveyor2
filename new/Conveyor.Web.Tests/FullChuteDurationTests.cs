using Conveyor.Web.Services;

namespace Conveyor.Web.Tests;

public sealed class FullChuteDurationTests
{
    [Fact]
    public void AccumulatesFullIntervalsIncludingCurrentIntervalWithoutDoubleCounting()
    {
        var duration = new FullChuteDuration();
        var start = DateTimeOffset.UtcNow;
        duration.Observe(false, start);
        duration.Observe(true, start.AddSeconds(10));
        duration.Observe(true, start.AddSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(10), duration.GetTotal(start.AddSeconds(20)));
        duration.Observe(false, start.AddSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(20), duration.GetTotal(start.AddSeconds(50)));
        duration.Observe(true, start.AddSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(25), duration.GetTotal(start.AddSeconds(65)));
        duration.Observe(null, start.AddSeconds(70));
        Assert.Equal(TimeSpan.FromSeconds(30), duration.GetTotal(start.AddSeconds(100)));
        duration.Observe(true, start.AddSeconds(110));
        Assert.Equal(TimeSpan.FromSeconds(40), duration.GetTotal(start.AddSeconds(120)));
    }
}
