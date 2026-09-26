using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class OpcDaPlcGatewayTests
{
    [Fact]
    public void SharedCountersAreExcludedFromControlReadsIncludingQualifiedAliases()
    {
        var monitored = new[] { "COLISDDE", "DEPART_SYSTEMES", "SHARE_NB_CHUTEPLEINE", "[NATIONEX]SHARE_NB_CODE42" };
        var polled = OpcDaConnection.PolledItemIds("NATIONEX", monitored, ["SHARE_NB_CHUTEPLEINE", "SHARE_NB_CODE42"]);
        Assert.Equal(2, polled.Count);
        Assert.Contains("[NATIONEX]COLISDDE", polled);
        Assert.Contains("[NATIONEX]DEPART_SYSTEMES", polled);
        Assert.Equal(4, OpcDaConnection.PolledItemIds("NATIONEX", monitored, null).Count);
        Assert.Empty(OpcDaConnection.PolledItemIds("NATIONEX", ["CUSTOM"], ["custom"]));
    }

    [Theory]
    [InlineData("NATIONEX", "COLISDDE", "[NATIONEX]COLISDDE")]
    [InlineData(" NATIONEX ", "DEPART_SYSTEMES", "[NATIONEX]DEPART_SYSTEMES")]
    [InlineData("NATIONEX", "[SECOND]TRANSFER", "[SECOND]TRANSFER")]
    [InlineData("", "COLISDDE", "COLISDDE")]
    public void ItemIdsPreserveExplicitTopics(string topic, string tag, string expected) =>
        Assert.Equal(expected, OpcDaConnection.ItemId(topic, tag));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" localhost ", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("REMOTE-OPC", false)]
    public void OpcHostDistinguishesLocalActivationFromDcom(string? host, bool expected) =>
        Assert.Equal(expected, OpcDaConnection.IsLocalHost(host));

    [Fact]
    public async Task InitialReadAndSubscriptionNormalizeBooleansAndRejectBadQuality()
    {
        var client = new FakeConnection();
        using var gateway = Create(() => client);
        var values = new List<string>();
        gateway.TagChanged += (_, value) => values.Add(value);
        await gateway.ConnectAsync(default);
        Assert.True(gateway.ReadsHealthy);
        client.Emit(true);
        client.Emit(99, false);
        Assert.Equal(["16", "1"], values);
        Assert.False(gateway.ReadsHealthy);
        Assert.True(gateway.IsConnected);
        client.Emit(false);
        Assert.True(gateway.ReadsHealthy);
        Assert.Equal("0", values.Last());
    }

    [Fact]
    public async Task OlderReadCannotOverwriteNewerNotification()
    {
        var client = new FakeConnection();
        using var gateway = Create(() => client);
        var values = new List<string>();
        gateway.TagChanged += (_, value) => values.Add(value);
        await gateway.ConnectAsync(default);
        client.DuringRead = () => client.Emit(39);
        await gateway.PingAsync(default);
        Assert.Equal(["16", "39"], values);
    }

    [Fact]
    public async Task ReconnectDoesNotReplayWritesAndManualDisconnectStaysDisconnected()
    {
        var first = new FakeConnection();
        var second = new FakeConnection();
        var connections = new Queue<FakeConnection>([first, second]);
        var clock = new Clock();
        using var gateway = Create(() => connections.Dequeue(), clock);
        await gateway.ConnectAsync(default);
        await gateway.SendChuteAsync("DEPART_SYSTEMES", 0, 1, default);
        first.IsConnected = false;
        Assert.True(await gateway.PingAsync(default));
        Assert.True(first.Disposed);
        Assert.Single(first.Writes);
        Assert.Empty(second.Writes);
        await gateway.DisconnectAsync();
        clock.Advance();
        Assert.False(await gateway.PingAsync(default));
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task ReadFailureSchedulesReconnectAndDoesNotCrashHealthMonitor()
    {
        var first = new FakeConnection();
        var second = new FakeConnection();
        var connections = new Queue<FakeConnection>([first, second]);
        var clock = new Clock();
        using var gateway = Create(() => connections.Dequeue(), clock);
        await gateway.ConnectAsync(default);
        first.FailRead = true;
        Assert.False(await gateway.PingAsync(default));
        Assert.True(first.Disposed);
        clock.Advance();
        Assert.True(await gateway.PingAsync(default));
    }

    [Fact]
    public async Task FailedWriteStopsRepetitionAndPropagatesError()
    {
        var client = new FakeConnection { FailWrite = true };
        using var gateway = Create(() => client);
        await gateway.ConnectAsync(default);
        await Assert.ThrowsAsync<IOException>(() => gateway.SendChuteAsync("COLISDDE", 39, 3, default));
        Assert.Single(client.Writes);
    }

    [Fact]
    public async Task SuccessfulWritesRespectConfiguredRepeatAndReadFaultDoesNotBlockStop()
    {
        var client = new FakeConnection();
        using var gateway = Create(() => client);
        await gateway.ConnectAsync(default);
        client.Emit(16, false);
        await gateway.SendChuteAsync("DEPART_SYSTEMES", 0, 1, default);
        await gateway.SendChuteAsync("COLISDDE", 39, 2, default);
        Assert.Equal(3, client.Writes.Count);
        Assert.Equal(("DEPART_SYSTEMES", 0), client.Writes[0]);
    }

    [Fact]
    public async Task MissingInitialTagAndBadValueDoNotReportHealthyReadback()
    {
        var client = new FakeConnection { OmitReads = true };
        using var gateway = Create(() => client);
        await gateway.ConnectAsync(default);
        Assert.False(gateway.ReadsHealthy);
        client.Emit(null);
        Assert.False(gateway.ReadsHealthy);
        client.Emit(new[] { 16, 39 });
        Assert.False(gateway.ReadsHealthy);
        client.Emit(39);
        Assert.True(gateway.ReadsHealthy);
    }

    [Fact]
    public async Task SubscriberExceptionDoesNotEscapeOpcCallback()
    {
        var client = new FakeConnection();
        using var gateway = Create(() => client);
        var received = 0;
        gateway.TagChanged += (_, _) => throw new InvalidOperationException();
        gateway.TagChanged += (_, _) => received++;
        await gateway.ConnectAsync(default);
        client.Emit(39);
        Assert.Equal(2, received);
    }

    [Fact]
    public async Task ShutdownWaitsForReadThenReleasesClientOnceAndIgnoresLateCallbacks()
    {
        var client = new FakeConnection();
        using var gateway = Create(() => client);
        var received = new List<string>();
        gateway.TagChanged += (_, value) => received.Add(value);
        await gateway.ConnectAsync(default);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DuringRead = () =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
        };
        var read = gateway.PingAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = gateway.DisconnectAsync();
        try
        {
            Assert.False(shutdown.IsCompleted);
            Assert.False(client.Disposed);
        }
        finally { release.Set(); }
        await read;
        await shutdown;
        var count = received.Count;
        client.Emit(39);
        await gateway.DisconnectAsync();
        gateway.Dispose();
        Assert.Equal(count, received.Count);
        Assert.Equal(1, client.DisposeCount);
        Assert.False(await gateway.PingAsync(default));
    }

    private static OpcDaPlcGateway Create(Func<IOpcDaConnection> factory, TimeProvider? clock = null) =>
        new(new PlcOptions { Protocol = "OpcDa" }, NullLogger<OpcDaPlcGateway>.Instance,
            ["COLISDDE"], factory, clock ?? new Clock());

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(5);
    }

    private sealed class FakeConnection : IOpcDaConnection
    {
        public bool IsConnected { get; set; }
        public bool Disposed, FailRead, FailWrite, OmitReads;
        public int DisposeCount;
        public Action? DuringRead;
        private DateTimeOffset _timestamp = DateTimeOffset.UtcNow;
        public event Action<IReadOnlyList<OpcDaReading>>? ValuesChanged;
        public readonly List<(string Tag, int Value)> Writes = [];
        public void Connect() => IsConnected = true;
        public Task<IReadOnlyList<OpcDaReading>> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (FailRead) throw new IOException("Read failed");
            var reading = new OpcDaReading("COLISDDE", 16, true, _timestamp);
            DuringRead?.Invoke();
            return Task.FromResult<IReadOnlyList<OpcDaReading>>(OmitReads ? [] : [reading]);
        }
        public Task WriteAsync(string tag, int value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Writes.Add((tag, value));
            if (FailWrite) throw new IOException("Write rejected");
            return Task.CompletedTask;
        }
        public void Emit(object? value, bool good = true)
        {
            _timestamp = _timestamp.AddSeconds(1);
            ValuesChanged?.Invoke([new("COLISDDE", value, good, _timestamp)]);
        }
        public void Dispose() { DisposeCount++; Disposed = true; IsConnected = false; }
    }
}
