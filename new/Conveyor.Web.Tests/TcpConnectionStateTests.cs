using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Conveyor.Web.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class TcpConnectionStateTests
{
    [Fact]
    public async Task ClientNotifiesBothObserversWithoutFramesAndReconnectsAfterRemoteClose()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var first = Channel.CreateUnbounded<bool>();
        var second = Channel.CreateUnbounded<bool>();
        var frames = Channel.CreateUnbounded<string>();
        TcpFrameClient? client = null;
        client = new("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, "\r\n",
            NullLogger.Instance, () =>
            {
                first.Writer.TryWrite(client!.Connected);
                second.Writer.TryWrite(client.Connected);
            });
        var run = client.RunAsync(frames.Writer, timeout.Token);
        try
        {
            using (var peer = await listener.AcceptTcpClientAsync(timeout.Token))
                await AssertBothAsync(true, first, second, timeout.Token);
            await AssertBothAsync(false, first, second, timeout.Token);
            using var reconnected = await listener.AcceptTcpClientAsync(timeout.Token);
            await AssertBothAsync(true, first, second, timeout.Token);
            Assert.False(frames.Reader.TryRead(out _));
        }
        finally
        {
            await timeout.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await AssertBothAsync(false, first, second, CancellationToken.None);
    }

    [Fact]
    public async Task ServerNotifiesBothObserversWithoutFramesAndAcceptsNextConnection()
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = Channel.CreateUnbounded<bool>();
        var second = Channel.CreateUnbounded<bool>();
        var frames = Channel.CreateUnbounded<string>();
        TcpFrameReceiver? receiver = null;
        receiver = new(port, "\r\n", NullLogger.Instance, () =>
        {
            first.Writer.TryWrite(receiver!.Connected);
            second.Writer.TryWrite(receiver.Connected);
        });
        var run = receiver.RunAsync(frames.Writer, timeout.Token);
        try
        {
            using (var peer = new TcpClient())
            {
                await peer.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                await AssertBothAsync(true, first, second, timeout.Token);
            }
            await AssertBothAsync(false, first, second, timeout.Token);
            using var next = new TcpClient();
            await next.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await AssertBothAsync(true, first, second, timeout.Token);
            Assert.False(frames.Reader.TryRead(out _));
        }
        finally
        {
            await timeout.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await AssertBothAsync(false, first, second, CancellationToken.None);
    }

    private static async Task AssertBothAsync(bool expected, Channel<bool> first, Channel<bool> second, CancellationToken token)
    {
        Assert.Equal(expected, await first.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(expected, await second.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
