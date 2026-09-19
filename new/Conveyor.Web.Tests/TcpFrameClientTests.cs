using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Conveyor.Web.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conveyor.Web.Tests;

public sealed class TcpFrameClientTests
{
    [Theory]
    [InlineData("\r", "CAM123")]
    [InlineData("\r\n", "12.5")]
    [InlineData("\u0003", "0010020030040056")]
    public async Task ClientReceivesFramesAndStopsOnCancellation(string delimiter, string frame)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var receiver = new TcpFrameClient("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port,
            delimiter, NullLogger.Instance);
        var channel = Channel.CreateUnbounded<string>();
        var run = receiver.RunAsync(channel.Writer, timeout.Token);
        try
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes(frame + delimiter + frame + delimiter), timeout.Token);
            Assert.Equal(frame, await channel.Reader.ReadAsync(timeout.Token));
            Assert.Equal(frame, await channel.Reader.ReadAsync(timeout.Token));
            Assert.True(receiver.Connected);
        }
        finally
        {
            await timeout.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(receiver.Connected);
    }
}
