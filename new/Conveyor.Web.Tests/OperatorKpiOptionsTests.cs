using Conveyor.Web.Options;

namespace Conveyor.Web.Tests;

public class OperatorKpiOptionsTests
{
    [Theory]
    [InlineData(2000, 3000, true)]
    [InlineData(0, 1000, true)]
    [InlineData(-1, 3000, false)]
    [InlineData(3000, 3000, false)]
    [InlineData(4000, 3000, false)]
    public void ChartThresholdsRequireNonNegativeAndOrderedValues(decimal yellow, decimal green, bool valid)
    {
        var options = new OperatorKpiOptions { ChartCadenceYellowFrom = yellow, ChartCadenceGreenAbove = green };
        Assert.Equal(valid, options.ValidationError() is null);
    }

    [Fact]
    public void ChartThresholdsSurviveSerializationAndOldSettingsKeepDefaults()
    {
        var defaults = System.Text.Json.JsonSerializer.Deserialize<OperatorKpiOptions>("{}")!;
        Assert.Equal(2000m, defaults.ChartCadenceYellowFrom);
        Assert.Equal(3000m, defaults.ChartCadenceGreenAbove);
        defaults.ChartCadenceYellowFrom = 2500;
        defaults.ChartCadenceGreenAbove = 4500;
        var restored = System.Text.Json.JsonSerializer.Deserialize<OperatorKpiOptions>(System.Text.Json.JsonSerializer.Serialize(defaults))!;
        Assert.Equal(2500m, restored.ChartCadenceYellowFrom);
        Assert.Equal(4500m, restored.ChartCadenceGreenAbove);
        Assert.Equal(1000m, restored.CadenceRedThrough);
        Assert.Equal(1500m, restored.CadenceGreenAbove);
    }

    [Theory]
    [InlineData(0, "critical")]
    [InlineData(1000, "critical")]
    [InlineData(1000.1, "warning")]
    [InlineData(1500, "warning")]
    [InlineData(1500.1, "success")]
    public void CadenceBoundaries(double rate, string tone) => Assert.Equal(tone, new OperatorKpiOptions().CadenceTone(rate));

    [Theory]
    [InlineData(9499, "critical")]
    [InlineData(9500, "warning")]
    [InlineData(9699, "warning")]
    [InlineData(9700, "success")]
    public void SortedBoundaries(long count, string tone) => Assert.Equal(tone, new OperatorKpiOptions().SortedTone(count, 10000));

    [Theory]
    [InlineData(0, "success")]
    [InlineData(149, "success")]
    [InlineData(150, "warning")]
    [InlineData(350, "warning")]
    [InlineData(351, "critical")]
    public void RateBoundaries(long count, string tone) => Assert.Equal(tone, new OperatorKpiOptions().RateTone(count, 10000));

    [Fact]
    public void MissingDataStaysNeutral()
    {
        var options = new OperatorKpiOptions();
        Assert.Equal("", options.CadenceTone(null));
        Assert.Equal("", options.SortedTone(0, 0));
        Assert.Equal("", options.RateTone(0, 0));
    }

    [Fact]
    public void CustomThresholdsAreUsedAndInvalidOrderingIsRejected()
    {
        var options = new OperatorKpiOptions { CadenceRedThrough = 500, CadenceGreenAbove = 900,
            SortedRedBelow = 90, SortedGreenFrom = 92, RateGreenBelow = 2, RateYellowThrough = 4 };
        Assert.Null(options.ValidationError());
        Assert.Equal("success", options.CadenceTone(1000));
        Assert.Equal("success", options.SortedTone(93, 100));
        Assert.Equal("success", options.RateTone(15, 1000));
        options.RateYellowThrough = 1;
        Assert.NotNull(options.ValidationError());
        options.RateYellowThrough = 4;
        options.SortedRedBelow = 94;
        Assert.NotNull(options.ValidationError());
        options.SortedRedBelow = 90;
        options.CadenceRedThrough = 1000;
        Assert.NotNull(options.ValidationError());
    }
}
