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
    [InlineData("\r\n\u0002Q00003\u0003")]
    [InlineData("\r\n\u0002Q000003\u0003")]
    public void Dimension_control_frames_are_identified(string frame)
    {
        Assert.True(SensorParsers.IsDimensionControlFrame(frame));
    }

    [Fact]
    public void Dimension_measurement_is_not_a_control_frame()
    {
        Assert.False(SensorParsers.IsDimensionControlFrame("\u00020000012400810052\u0003"));
    }

    [Theory]
    [InlineData("\u0002  12.50\r\n", "Delimited", 12.50)]
    [InlineData("000004.750      ", "Fixed16From0", 4.750)]
    [InlineData("X00004.750     ", "Fixed16From1", 4.750)]
    public void Scale_formats_are_supported(string frame, string protocol, decimal expected)
    {
        Assert.Equal(expected, SensorParsers.ParseWeight(frame, protocol));
    }

    [Theory]
    [InlineData("         4\u0003\u001F\u0003\u0003\u0003\u0003\u000B\u0002\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u0003\u007F\u0002001.65LB\r\n")]
    [InlineData("garbage\u0002001.65LB")]
    public void Stx_to_crlf_scale_format_uses_only_the_final_payload(string frame)
    {
        Assert.Equal(1.65m, SensorParsers.ParseWeight(frame, "StxToCrLf"));
    }

    [Fact]
    public void Stx_to_crlf_scale_format_requires_stx()
    {
        Assert.Null(SensorParsers.ParseWeight("001.65LB\r\n", "StxToCrLf"));
    }

    [Theory]
    [InlineData("\u0002022.30LB\r\n   ", "022.30LB")]
    [InlineData("garbage\u0002001.65LB\r\n", "001.65LB")]
    [InlineData("  4.75 lb  ", "4.75 lb")]
    public void Weight_display_keeps_only_the_readable_payload(string frame, string expected)
    {
        Assert.Equal(expected, SensorParsers.FormatWeightForDisplay(frame));
    }
}
