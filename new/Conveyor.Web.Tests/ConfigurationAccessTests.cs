using Conveyor.Web.Services;

namespace Conveyor.Web.Tests;

public sealed class ConfigurationAccessTests
{
    [Theory]
    [InlineData("3505", true)]
    [InlineData("3504", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PinIsValidatedServerSide(string? pin, bool expected)
    {
        Assert.Equal(expected, ConfigurationAccess.VerifyPin(pin));
    }
}
