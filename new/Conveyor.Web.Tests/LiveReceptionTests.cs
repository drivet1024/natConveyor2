using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Conveyor.Web.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class LiveReceptionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Raw_reception_is_published_before_blocked_processing_and_keeps_delimiters(bool clientMode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var output = Channel.CreateBounded<string>(1);
        await output.Writer.WriteAsync("processing queue full", timeout.Token);
        var received = Channel.CreateUnbounded<string>();
        void OnFrame(string raw) => received.Writer.TryWrite(raw);
        Task run;
        TcpClient peer;
        if (clientMode)
        {
            var client = new TcpFrameClient("127.0.0.1", port, "\u0003", NullLogger.Instance, frameReceived: OnFrame);
            run = client.RunAsync(output.Writer, timeout.Token);
            peer = await listener.AcceptTcpClientAsync(timeout.Token);
        }
        else
        {
            listener.Stop();
            var server = new TcpFrameReceiver(port, "\u0003", NullLogger.Instance, frameReceived: OnFrame);
            run = server.RunAsync(output.Writer, timeout.Token);
            peer = new TcpClient();
            await peer.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        }
        try
        {
            // Even a frame that cannot be parsed must be visible without waiting
            // for the processing queue, and with its original control characters.
            const string raw = "\u0002invalid measurement\u0003";
            await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes(raw), timeout.Token);
            Assert.Equal(raw, await received.Reader.ReadAsync(timeout.Token));
            Assert.Equal("processing queue full", await output.Reader.ReadAsync(timeout.Token));
            Assert.Equal("invalid measurement", await output.Reader.ReadAsync(timeout.Token));
            await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\u0002\u0003"), timeout.Token);
            Assert.Equal("\u0002\u0003", await received.Reader.ReadAsync(timeout.Token));
        }
        finally
        {
            await timeout.CancelAsync();
            peer.Dispose();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
