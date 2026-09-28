using Conveyor.Web.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Conveyor.Web.Tests;

public sealed class OpcDaDiagnosticsTests
{
    [Fact]
    public void SilenceAlertsAreLimitedAndRecoveryReportsRejectedTimestamps()
    {
        var clock = new Clock();
        var logs = new Logs();
        using var diagnostics = new OpcDaDiagnostics(logs, clock, ["M31"]);
        diagnostics.Start();
        var timestamp = clock.GetUtcNow();
        diagnostics.Received(new("M31", 40, true, timestamp), "subscription", timestamp, true);
        clock.Advance(31);
        diagnostics.Received(new("M31", 41, true, timestamp.AddSeconds(-1)), "control-read", timestamp, false);
        diagnostics.ControlCompleted(clock.GetUtcNow(), 1);
        diagnostics.Check();
        diagnostics.Check();
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("M31", warning.Fields["Tag"]);
        Assert.Equal(1, warning.Fields["RejectedCount"]);
        Assert.Equal("control-read", warning.Fields["Source"]);
        clock.Advance(30);
        diagnostics.ControlCompleted(clock.GetUtcNow(), 1);
        diagnostics.Check();
        Assert.Equal(2, logs.Entries.Count(entry => entry.Level == LogLevel.Warning));
        diagnostics.Received(new("M31", 42, true, timestamp), "subscription", timestamp, true);
        var recovery = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Equal(61d, recovery.Fields["SilenceSeconds"]);
        Assert.Equal(1, recovery.Fields["RejectedCount"]);
    }

    [Fact]
    public void IdenticalValuesAreFreshAndSubscriptionOnlyTagsDoNotRaiseSilenceAlarms()
    {
        var clock = new Clock();
        var logs = new Logs();
        using var diagnostics = new OpcDaDiagnostics(logs, clock, ["M31"]);
        diagnostics.Start();
        var timestamp = clock.GetUtcNow();
        diagnostics.Received(new("COUNTER", 0, true, timestamp), "subscription", timestamp, true);
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(5);
            diagnostics.Received(new("M31", 40, true, timestamp), "control-read", timestamp, true);
            diagnostics.ControlCompleted(clock.GetUtcNow(), 1);
            diagnostics.Check();
        }
        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Information);
    }

    [Fact]
    public void WatchdogReportsStuckOperationEvenWithoutAnotherCompletedRead()
    {
        var clock = new Clock();
        var logs = new Logs();
        using var diagnostics = new OpcDaDiagnostics(logs, clock, []);
        diagnostics.Start();
        diagnostics.Operation("lecture de contrôle");
        diagnostics.Skipped();
        diagnostics.Skipped();
        clock.Advance(35);
        diagnostics.Check();
        diagnostics.Check();
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("lecture de contrôle", warning.Fields["Operation"]);
        Assert.Equal(2, warning.Fields["SkippedCount"]);
        diagnostics.ControlCompleted(clock.GetUtcNow().AddSeconds(-35), 0);
        Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Information);
        diagnostics.Stop();
        var count = logs.Entries.Count;
        clock.Advance(60);
        diagnostics.Check();
        Assert.Equal(count, logs.Entries.Count);
    }

    [Fact]
    public void SubscriptionOnlyTagsAlertOnRejectedTimestampsButNotSimpleSilence()
    {
        var clock = new Clock();
        var logs = new Logs();
        using var diagnostics = new OpcDaDiagnostics(logs, clock, [], ["M31"]);
        diagnostics.Start();
        var timestamp = clock.GetUtcNow();
        diagnostics.Received(new("M31", 40, true, timestamp), "subscription", timestamp, true);
        clock.Advance(31);
        diagnostics.ControlCompleted(clock.GetUtcNow(), 0);
        diagnostics.Check();
        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Warning);
        diagnostics.Received(new("M31", 41, true, timestamp.AddSeconds(-1)), "subscription", timestamp, false);
        diagnostics.Check();
        Assert.Equal("M31", Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning).Fields["Tag"]);
        diagnostics.Received(new("M31", 42, true, timestamp.AddSeconds(1)), "subscription", timestamp, true);
        Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Information);
    }

    [Fact]
    public void BadQualityDoesNotCountAsAcceptedReception()
    {
        var clock = new Clock();
        var logs = new Logs();
        using var diagnostics = new OpcDaDiagnostics(logs, clock, ["M31"]);
        diagnostics.Start();
        clock.Advance(31);
        diagnostics.Received(new("M31", 40, false, clock.GetUtcNow(), "bad quality"), "subscription", DateTimeOffset.MinValue, false);
        diagnostics.ControlCompleted(clock.GetUtcNow(), 1);
        diagnostics.Check();
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(false, warning.Fields["Good"]);
        Assert.NotNull(warning.Fields["LastArrival"]);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class Logs : ILogger
    {
        public List<(LogLevel Level, Dictionary<string, object?> Fields)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
    }
}
