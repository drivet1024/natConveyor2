using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Conveyor.Web.Infrastructure;

public sealed class TcpFrameClient(string host, int port, string delimiter, ILogger logger)
{
    public bool Connected { get; private set; }

    public async Task RunAsync(ChannelWriter<string> output, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(host, port, token);
                    Connected = true;
                    await ReadAsync(client, output, delimiter, token);
                }
                catch (Exception exception) when (exception is IOException or SocketException)
                {
                    logger.LogWarning(exception, "Dimensionneur {Host}:{Port} indisponible", host, port);
                }
                finally { Connected = false; }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { output.TryComplete(); }
    }

    private static async Task ReadAsync(TcpClient client, ChannelWriter<string> output, string delimiter, CancellationToken token)
    {
        await using var stream = client.GetStream();
        var buffer = new byte[1024];
        var pending = new StringBuilder();
        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            pending.Append(Encoding.ASCII.GetString(buffer, 0, read));
            while (true)
            {
                var text = pending.ToString();
                var end = text.IndexOf(delimiter, StringComparison.Ordinal);
                if (end < 0) break;
                var frame = text[..end].Trim('\u0002', '\u0003', '\r', '\n');
                pending.Remove(0, end + delimiter.Length);
                if (!string.IsNullOrWhiteSpace(frame)) await output.WriteAsync(frame, token);
            }
        }
    }
}
