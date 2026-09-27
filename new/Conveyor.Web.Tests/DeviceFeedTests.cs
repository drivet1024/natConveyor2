using Conveyor.Web.Components.Layout;
using Conveyor.Web.Domain;

namespace Conveyor.Web.Tests;

public sealed class DeviceFeedTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ScaleIsLateOnlyWhenReceivedStrictlyAfterCamera(int offsetMs, bool expected)
    {
        var cameraAt = DateTimeOffset.UtcNow;
        var scale = new DeviceReception("2.5", cameraAt.AddMilliseconds(offsetMs), 1);
        Assert.Equal(expected, DeviceFeed.IsAfterCamera(scale, new("12345678901", cameraAt, 1)));
    }

    [Fact]
    public void MissingScaleOrCameraDoesNotShowLateIndicator()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(DeviceFeed.IsAfterCamera(null, new("12345678901", now, 1)));
        Assert.False(DeviceFeed.IsAfterCamera(new("2.5", now, 1), null));
        Assert.False(DeviceFeed.IsAfterCamera(null, null));
    }
}
