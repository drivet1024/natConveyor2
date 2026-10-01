using Conveyor.Web.Services;

namespace Conveyor.Web.Tests;

public sealed class Code68TimingTests
{
    [Theory]
    [InlineData(1256, "DÉLAI = TRANSFERT − DDE = -1256 ms\n  Envoi DDE confirmé 1256 ms APRÈS la mise à jour du transfert 68.")]
    [InlineData(-804, "DÉLAI = TRANSFERT − DDE = +804 ms\n  Envoi DDE confirmé 804 ms AVANT la mise à jour du transfert 68.")]
    [InlineData(0, "DÉLAI = TRANSFERT − DDE = 0 ms\n  Envoi DDE confirmé et mise à jour du transfert 68 au même instant (0 ms).")]
    public void ReportsDirectionAndElapsedMilliseconds(int elapsedMs, string expected)
    {
        var transfer = new DateTimeOffset(2026, 10, 1, 18, 19, 10, 735, TimeSpan.FromHours(-4));
        Assert.Equal(expected, LineController.FormatTransferDispatchTiming(transfer, transfer.AddMilliseconds(elapsedMs)));
    }

    [Fact]
    public void MissingDispatchDoesNotInventADelay()
    {
        Assert.Contains("Écart indisponible", LineController.FormatTransferDispatchTiming(DateTimeOffset.UtcNow, null));
    }
}
