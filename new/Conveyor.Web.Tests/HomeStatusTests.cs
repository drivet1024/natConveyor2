using Conveyor.Web.Components.Pages;

namespace Conveyor.Web.Tests;

public sealed class HomeStatusTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(null, true, null)]
    [InlineData(false, null, null)]
    [InlineData(null, null, null)]
    [InlineData(true, null, false)]
    [InlineData(null, false, false)]
    public void MobileLineRequiresNoScaleFaultAndRunningConveyor(bool? fault, bool? running, bool? expected)
    {
        Assert.Equal(expected, Home.ResolveMobileLineRunning(fault, running));
    }
}
