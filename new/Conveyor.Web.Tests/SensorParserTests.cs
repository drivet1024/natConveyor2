using Conveyor.Web.Services;

namespace Conveyor.Web.Tests;

public sealed class SensorParserTests
{
    [Fact]
    public void Dimension_frame_is_parsed_in_tenths()
    {
        var result = SensorParsers.ParseDimension("0000012400810052");
        Assert.NotNull(result);
        Assert.Equal(12.4m, result.Length);
        Assert.Equal(8.1m, result.Width);
        Assert.Equal(5.2m, result.Height);
    }

    [Theory]
    [InlineData("\u0002  12.50\r\n", "Delimited", 12.50)]
    [InlineData("000004.750      ", "Fixed16From0", 4.750)]
    [InlineData("X00004.750     ", "Fixed16From1", 4.750)]
    public void Scale_formats_are_supported(string frame, string protocol, decimal expected)
    {
        Assert.Equal(expected, SensorParsers.ParseWeight(frame, protocol));
    }
}
