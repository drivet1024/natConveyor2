using Conveyor.Web.Infrastructure;
using Conveyor.Web.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class DdePlcGatewayTests
{
    [Fact]
    public async Task FailedSubscriptionIsRetriedWithoutReplayingWrites()
    {
        var client = new FakeConnection { FailSubscribe = true };
        using var gateway = Create(() => client, new Clock());
        await gateway.ConnectAsync(default);
        Assert.True(gateway.IsConnected);
        Assert.False(gateway.ReadsHealthy);
        await gateway.SendChuteAsync("DEPART_SYSTEMES", 0, 1, default);
        client.FailSubscribe = false;
        Assert.True(await gateway.PingAsync(default));
        Assert.Equal(2, client.Subscriptions);
        Assert.Single(client.Writes);
    }

    [Fact]
    public async Task ChangedDirectReadRepairsSilentSubscriptionAndPublishesFallback()
    {
        var client = new FakeConnection();
        using var gateway = Create(() => client, new Clock());
        var values = new List<string>();
        gateway.TagChanged += (_, value) => values.Add(value);
        await gateway.ConnectAsync(default);
        client.Emit("16");
        client.Value = "39\0";
        Assert.True(await gateway.PingAsync(default));
        Assert.Equal(["16", "39"], values);
        Assert.Equal(1, client.Stops);
        Assert.Equal(2, client.Subscriptions);
        Assert.Empty(client.Writes);
    }

    [Fact]
    public async Task RepeatedSilentChangesReconnectAndNewNotificationsReachSubscribersWithoutReplayingWrites()
    {
        var first = new FakeConnection();
        var second = new FakeConnection();
        var connections = new Queue<FakeConnection>([first, second]);
        var clock = new Clock();
        using var gateway = Create(() => connections.Dequeue(), clock);
        var values = new List<string>();
        gateway.TagChanged += (_, value) => values.Add(value);
        await gateway.ConnectAsync(default);
        await gateway.SendChuteAsync("COLISDDE", 5, 1, default);
        first.Emit("5");
        foreach (var value in new[] { "68", "16", "39" })
        {
            first.Value = value;
            Assert.True(await gateway.PingAsync(default));
            clock.Advance();
        }
        Assert.True(first.Disposed);
        Assert.Equal(2, first.Stops);
        Assert.Equal(1, second.Subscriptions);
        second.Emit("7");
        first.Emit("99");
        Assert.Equal(["5", "68", "16", "39", "7"], values);
        Assert.Single(first.Writes);
        Assert.Empty(second.Writes);
    }

    [Fact]
    public async Task ResumedNotificationsResetSilentChangeCount()
    {
        var client = new FakeConnection();
        var clock = new Clock();
        var created = 0;
        using var gateway = Create(() => { created++; return client; }, clock);
        await gateway.ConnectAsync(default);
        client.Emit("5");
        for (var cycle = 0; cycle < 3; cycle++)
        {
            foreach (var value in new[] { "68", "16" })
            {
                client.Value = value;
                Assert.True(await gateway.PingAsync(default));
                clock.Advance();
            }
            client.Emit("5");
        }
        Assert.Equal(1, created);
        Assert.False(client.Disposed);
        Assert.Equal(6, client.Stops);
    }

    [Fact]
    public async Task ConstantValueRefreshesReceptionWithoutRestartingSubscription()
    {
        var client = new FakeConnection();
        var clock = new Clock();
        using var gateway = Create(() => client, clock);
        var receptions = 0;
        gateway.TagChanged += (_, _) => receptions++;
        await gateway.ConnectAsync(default);
        client.Emit("16");
        await gateway.PingAsync(default);
        await gateway.PingAsync(default); // Second line, same control interval.
        clock.Advance();
        await gateway.PingAsync(default);
        Assert.Equal(3, receptions);
        Assert.Equal(2, client.Requests);
        Assert.Equal(1, client.Subscriptions);
        Assert.Equal(0, client.Stops);
    }

    [Fact]
    public async Task NotificationDuringDirectReadWinsOverOlderRequestResult()
    {
        var client = new FakeConnection();
        using var gateway = Create(() => client, new Clock());
        var values = new List<string>();
        gateway.TagChanged += (_, value) => values.Add(value);
        await gateway.ConnectAsync(default);
        client.Emit("16");
        client.DuringRequest = () => client.Emit("39");
        await gateway.PingAsync(default);
        Assert.Equal(["16", "39"], values);
        Assert.Equal(0, client.Stops);
    }

    [Fact]
    public async Task ReadFailuresReconnectAfterThresholdAndDoNotReplayCommands()
    {
        var first = new FakeConnection { FailRequest = true };
        var second = new FakeConnection();
        var clock = new Clock();
        var connections = new Queue<FakeConnection>([first, second]);
        using var gateway = Create(() => connections.Dequeue(), clock);
        await gateway.ConnectAsync(default);
        await gateway.SendChuteAsync("COLISDDE", 39, 1, default);
        for (var index = 0; index < 3; index++)
        {
            Assert.False(await gateway.PingAsync(default));
            clock.Advance();
        }
        Assert.True(first.Disposed);
        Assert.True(await gateway.PingAsync(default));
        Assert.Single(first.Writes);
        Assert.Empty(second.Writes);
    }

    [Fact]
    public async Task RemoteDisconnectReconnectsButExplicitDisconnectDoesNot()
    {
        var first = new FakeConnection();
        var second = new FakeConnection();
        var connections = new Queue<FakeConnection>([first, second]);
        var clock = new Clock();
        using var gateway = Create(() => connections.Dequeue(), clock);
        await gateway.ConnectAsync(default);
        first.IsConnected = false;
        Assert.True(await gateway.PingAsync(default));
        Assert.True(first.Disposed);
        await gateway.DisconnectAsync();
        clock.Advance();
        Assert.False(await gateway.PingAsync(default));
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task InitialConnectionFailureIsRetriedByHealthCheck()
    {
        var first = new FakeConnection { FailConnect = true };
        var second = new FakeConnection();
        var connections = new Queue<FakeConnection>([first, second]);
        using var gateway = Create(() => connections.Dequeue(), new Clock());
        await Assert.ThrowsAsync<IOException>(() => gateway.ConnectAsync(default));
        Assert.True(first.Disposed);
        Assert.True(await gateway.PingAsync(default));
    }

    [Fact]
    public async Task BadSubscriberDoesNotStopOtherSubscribersOrLaterNotifications()
    {
        var client = new FakeConnection();
        using var gateway = Create(() => client, new Clock());
        var values = new List<string>();
        gateway.TagChanged += (_, _) => throw new InvalidOperationException("UI failure");
        gateway.TagChanged += (_, value) => values.Add(value);
        await gateway.ConnectAsync(default);
        client.Emit("16");
        client.Emit("39");
        Assert.Equal(["16", "39"], values);
    }

    [Fact]
    public async Task FailedSubscriptionStopRecreatesConversationOnNextProbe()
    {
        var first = new FakeConnection { FailStop = true };
        var second = new FakeConnection();
        var connections = new Queue<FakeConnection>([first, second]);
        var clock = new Clock();
        using var gateway = Create(() => connections.Dequeue(), clock);
        await gateway.ConnectAsync(default);
        first.Emit("39");
        Assert.False(await gateway.PingAsync(default));
        Assert.True(first.Disposed);
        clock.Advance();
        Assert.True(await gateway.PingAsync(default));
    }

    [Fact]
    public async Task FailedTagAndFailedConversationDisposalDoNotStopRecovery()
    {
        var first = new FakeConnection { FailStop = true, FailDispose = true };
        var second = new FakeConnection();
        var connections = new Queue<FakeConnection>([first, second]);
        var clock = new Clock();
        using var gateway = Create(() => connections.Dequeue(), clock);
        await gateway.ConnectAsync(default);
        first.Emit("39");

        Assert.False(await gateway.PingAsync(default));

        clock.Advance();
        Assert.True(await gateway.PingAsync(default));
        Assert.True(second.IsConnected);
    }

    private static DdePlcGateway Create(Func<IDdeConnection> create, TimeProvider time) =>
        new(new PlcOptions(), NullLogger<DdePlcGateway>.Instance, ["COLISDDE"], create, time);

    [Fact]
    public async Task OneInvalidTagKeepsHealthyTagsAndConnectionOperational()
    {
        var client = new FakeConnection();
        client.FailedTags.Add("INVALID");
        var clock = new Clock();
        var created = 0;
        using var gateway = new DdePlcGateway(new(), NullLogger<DdePlcGateway>.Instance,
            ["COLISDDE", "INVALID"], () => { created++; return client; }, clock);
        await gateway.ConnectAsync(default);
        for (var index = 0; index < 8; index++)
        {
            Assert.True(await gateway.PingAsync(default));
            clock.Advance();
        }
        Assert.Equal(1, created);
        Assert.True(gateway.ReadsHealthy);
        Assert.True(gateway.IsConnected);
        client.FailedTags.Clear();
        await gateway.PingAsync(default);
        clock.Advance();
        Assert.True(await gateway.PingAsync(default));
    }

    [Fact]
    public async Task HealthCheckSkipsBusyWriterAndCancellationDoesNotDisconnect()
    {
        var client = new FakeConnection();
        using var gateway = Create(() => client, new Clock());
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DuringPoke = () => { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        await gateway.ConnectAsync(default);
        var send = gateway.SendChuteAsync("COLISDDE", 39, 1, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await gateway.PingAsync(default).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(0, client.Requests);
        }
        finally { release.Set(); await send; }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.PingAsync(canceled.Token));
        Assert.True(gateway.IsConnected);
        Assert.Single(client.Writes);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance() => _now = _now.AddSeconds(5);
    }

    private sealed class FakeConnection : IDdeConnection
    {
        public bool IsConnected { get; set; }
        public event Action<string, string>? Advise;
        public bool FailConnect, FailSubscribe, FailRequest, FailStop, FailDispose, Disposed;
        public int Subscriptions, Stops, Requests;
        public string Value = "16";
        public Action? DuringRequest;
        public Action? DuringPoke;
        public HashSet<string> FailedTags = [];
        public List<(string Tag, string Value)> Writes = [];
        public void Connect()
        {
            if (FailConnect) throw new IOException("No DDE server");
            IsConnected = true;
        }
        public void Emit(string value) => Advise?.Invoke("COLISDDE", value);
        public void StartAdvise(string tag, int timeout)
        {
            Subscriptions++;
            if (FailSubscribe) throw new IOException("Subscription failed");
        }
        public void StopAdvise(string tag, int timeout)
        {
            Stops++;
            if (FailStop) throw new IOException("Stop failed");
        }
        public string Request(string tag, int timeout)
        {
            Requests++;
            if (FailRequest || FailedTags.Contains(tag)) throw new IOException("Read timeout");
            DuringRequest?.Invoke();
            return Value;
        }
        public void Poke(string tag, string value, int timeout) { DuringPoke?.Invoke(); Writes.Add((tag, value)); }
        public void Dispose()
        {
            Disposed = true;
            IsConnected = false;
            if (FailDispose) throw new IOException("Dispose failed");
        }
    }
}
