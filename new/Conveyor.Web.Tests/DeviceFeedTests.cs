using Conveyor.Web.Components.Layout;
using Conveyor.Web.Domain;

namespace Conveyor.Web.Tests;

public sealed class DeviceFeedTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ScaleIsLateOnlyWhenReceivedStrictlyAfterDispatch(int offsetMs, bool expected)
    {
        var sentAt = DateTimeOffset.UtcNow;
        var input = new DeviceReception("2.5", sentAt.AddMilliseconds(offsetMs), 1);
        Assert.Equal(expected, DeviceFeed.IsAfterDispatch(input, new(sentAt, 4, 120, 1)));
    }

    [Fact]
    public void MissingReceptionOrDispatchDoesNotShowLateWarning()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(DeviceFeed.IsAfterDispatch(null, new(now, 4, 120, 1)));
        Assert.False(DeviceFeed.IsAfterDispatch(new("2.5", now, 1), null));
        Assert.False(DeviceFeed.IsAfterDispatch(null, null));
    }
}
