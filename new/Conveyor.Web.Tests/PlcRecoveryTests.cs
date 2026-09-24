using Conveyor.Web.Services;

namespace Conveyor.Web.Tests;

public sealed class PlcRecoveryTests
{
    [Fact]
    public async Task PersistentLossRestartsOnceAndRecoveryRequiresThirtyStableSeconds()
    {
        var fixture = new Fixture();
        await fixture.Check(0, healthy: false);
        await fixture.Check(44, healthy: false);
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Restarts);
        await fixture.Check(45, healthy: false);
        Assert.Equal(1, fixture.Restarts);
        await fixture.Check(200, healthy: false);
        Assert.Equal(1, fixture.Restarts);
        Assert.Equal(3, fixture.Messages.Count);
        await fixture.Check(201, healthy: true);
        await fixture.Check(220, healthy: true);
        Assert.Equal(3, fixture.Messages.Count);
        await fixture.Check(231, healthy: true);
        Assert.Contains("stable", fixture.Messages.Last());
        await fixture.Check(240, healthy: false);
        await fixture.Check(285, healthy: false);
        Assert.Equal(1, fixture.Restarts); // Five-minute cooldown survives a new incident.
        await fixture.Check(344, healthy: false);
        Assert.Equal(1, fixture.Restarts);
        await fixture.Check(345, healthy: false);
        Assert.Equal(2, fixture.Restarts);
    }

    [Fact]
    public async Task VoluntaryDisconnectAndBriefLossDoNotAlertOrRestart()
    {
        var fixture = new Fixture();
        await fixture.Check(0, healthy: false, requested: false);
        await fixture.Check(200, healthy: false, requested: false);
        await fixture.Check(201, healthy: false);
        await fixture.Check(210, healthy: true);
        await fixture.Check(211, healthy: false);
        await fixture.Check(220, healthy: true);
        Assert.Empty(fixture.Messages);
        Assert.Equal(0, fixture.Restarts);
    }

    [Fact]
    public async Task FailureReportsOnceWithoutRestartLoop()
    {
        var fixture = new Fixture { Fail = true };
        await fixture.Check(0, healthy: false);
        await fixture.Check(45, healthy: false);
        await fixture.Check(600, healthy: false);
        await fixture.Check(900, healthy: true);
        await fixture.Check(910, healthy: false); // Brief reconnection must not rearm.
        await fixture.Check(955, healthy: false);
        Assert.Equal(1, fixture.Restarts);
        Assert.Equal(1, fixture.Errors);
        Assert.Contains("Échec", fixture.Messages.Last());
    }

    [Fact]
    public async Task DisablingAutomaticRestartStillSendsDisconnectionSms()
    {
        var fixture = new Fixture();
        await fixture.Check(0, healthy: false, autoRestart: false);
        await fixture.Check(45, healthy: false, autoRestart: false);
        Assert.Single(fixture.Messages);
        Assert.Equal(0, fixture.Restarts);
    }

    private sealed class Fixture
    {
        private readonly PlcRecoveryIncident _incident = new();
        public int Restarts { get; private set; }
        public int Errors { get; private set; }
        public bool Fail { get; set; }
        public List<string> Messages { get; } = [];
        public Task Check(int seconds, bool healthy, bool requested = true, bool autoRestart = true) =>
            _incident.CheckAsync(DateTimeOffset.UnixEpoch.AddSeconds(seconds), requested, healthy, autoRestart,
                () =>
                {
                    Restarts++;
                    return Fail ? Task.FromException<string>(new InvalidOperationException("Test"))
                        : Task.FromResult("RSLinx relancé ; reconnexion en attente");
                }, Messages.Add, _ => Errors++);
    }
}
