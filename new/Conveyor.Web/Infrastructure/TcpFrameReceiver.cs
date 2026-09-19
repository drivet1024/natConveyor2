using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Conveyor.Web.Infrastructure;

public sealed class TcpFrameReceiver(int port, string delimiter, ILogger logger,
    Action? connectionChanged = null, string deviceName = "Appareil TCP", Action<string>? frameReceived = null)
{
    private TcpListener? _listener;
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
        _listener = new TcpListener(IPAddress.Any, port);
        try
        {
            _listener.Start();
            logger.LogInformation("{Device} : écoute TCP démarrée sur le port {Port}; en attente de connexion", deviceName, port);
            while (!token.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(token);
                logger.LogInformation("{Device} connecté depuis {Remote} sur le port {Port} (mode serveur)", deviceName, client.Client.RemoteEndPoint, port);
                SetConnected(true);
                try
                {
                    await ReadClientAsync(client, output, delimiter, token);
                    if (!token.IsCancellationRequested)
                        logger.LogWarning("{Device} : connexion fermée par l’appareil sur le port {Port}", deviceName, port);
                }
                catch (Exception exception) when (exception is IOException or SocketException)
                {
                    logger.LogWarning(exception, "{Device} : connexion TCP perdue sur le port {Port}", deviceName, port);
                }
                finally { SetConnected(false); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SocketException exception)
        {
            if (!token.IsCancellationRequested)
                logger.LogError(exception, "{Device} : impossible d’écouter sur le port {Port} (erreur {SocketError})", deviceName, port, exception.SocketErrorCode);
        }
        finally { _listener.Stop(); SetConnected(false); output.TryComplete(); }
    }

    private async Task ReadClientAsync(TcpClient client, ChannelWriter<string> output, string delimiter, CancellationToken token)
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

    public void Stop() => _listener?.Stop();
}
