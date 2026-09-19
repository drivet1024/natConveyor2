using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Conveyor.Web.Infrastructure;

public sealed class TcpFrameClient(string host, int port, string delimiter, ILogger logger,
    Action? connectionChanged = null, string deviceName = "Appareil TCP", Action<string>? frameReceived = null)
{
    private volatile bool _connected;
    public bool Connected => _connected;

    private void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        connectionChanged?.Invoke();
    }

    public async Task RunAsync(ChannelWriter<string> output, CancellationToken token)
    {
        logger.LogInformation("{Device} : démarrage TCP en mode client vers {Host}:{Port}; connexion en attente", deviceName, host, port);
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(host, port, token);
                    logger.LogInformation("{Device} connecté à {Host}:{Port} (mode client)", deviceName, host, port);
                    SetConnected(true);
                    await ReadAsync(client, output, delimiter, token);
                    if (!token.IsCancellationRequested)
                        logger.LogWarning("{Device} : connexion fermée par le serveur {Host}:{Port}; nouvelle tentative dans 2 s", deviceName, host, port);
                }
                catch (Exception exception) when (exception is IOException or SocketException)
                {
                    logger.LogWarning(exception, "{Device} {Host}:{Port} indisponible", deviceName, host, port);
                }
                finally { SetConnected(false); }
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { output.TryComplete(); }
    }

    private async Task ReadAsync(TcpClient client, ChannelWriter<string> output, string delimiter, CancellationToken token)
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
                frameReceived?.Invoke(text[..(end + delimiter.Length)]);
                var frame = text[..end].Trim('\u0002', '\u0003', '\r', '\n');
                pending.Remove(0, end + delimiter.Length);
                if (!string.IsNullOrWhiteSpace(frame)) await output.WriteAsync(frame, token);
            }
        }
    }
}
