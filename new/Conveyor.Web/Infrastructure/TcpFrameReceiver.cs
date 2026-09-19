using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Conveyor.Web.Infrastructure;

public sealed class TcpFrameReceiver(int port, string delimiter, ILogger logger)
{
    private TcpListener? _listener;
    public bool Connected { get; private set; }

    public async Task RunAsync(ChannelWriter<string> output, CancellationToken token)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        logger.LogInformation("Écoute TCP démarrée sur le port {Port}", port);
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(token);
                Connected = true;
                try
                {
                    await ReadClientAsync(client, output, delimiter, token);
                }
                catch (Exception exception) when (exception is IOException or SocketException)
                {
                    logger.LogWarning(exception, "Connexion TCP perdue sur le port {Port}", port);
                }
                finally { Connected = false; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _listener.Stop(); Connected = false; output.TryComplete(); }
    }

    private static async Task ReadClientAsync(TcpClient client, ChannelWriter<string> output, string delimiter, CancellationToken token)
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

    public void Stop() => _listener?.Stop();
}
