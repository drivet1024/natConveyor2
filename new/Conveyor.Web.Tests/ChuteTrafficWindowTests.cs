using Conveyor.Web.Domain;
using Conveyor.Web.Services;

namespace Conveyor.Web.Tests;

public class ChuteTrafficWindowTests
{
    [Fact]
    public void WindowExpiresWhileNoNewParcelsArrive()
    {
        var window = new ChuteTrafficWindow();
        var now = DateTimeOffset.UtcNow;
        window.Add(new(now, 21, 0, 1));
        Assert.Equal(1, window.Counts(now.AddMinutes(14))[21]);
        Assert.Empty(window.Counts(now.AddMinutes(15)));
    }

    [Fact]
    public void FallbackMovesParcelWithoutDoubleCounting()
    {
        var window = new ChuteTrafficWindow();
        var now = DateTimeOffset.UtcNow;
        window.Add(new(now, 21, 0, 1) { ParcelKey = "parcel" });
        window.Add(new(now.AddSeconds(1), 98, 0, 2) { ParcelKey = "parcel" });
        var counts = window.Counts(now.AddSeconds(2));
        Assert.Single(counts);
        Assert.Equal(1, counts[98]);
    }

    [Fact]
    public void TrafficIsNotLimitedToAnimationBuffer()
    {
        var window = new ChuteTrafficWindow();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 1000; i++) window.Add(new(now, 21, 0, i));
        Assert.Equal(1000, window.Counts(now)[21]);
    }
}
