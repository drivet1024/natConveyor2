using System.Net.Sockets;
using System.Text;
using Conveyor.Web.Options;
using Conveyor.Web.Services;

namespace Conveyor.Web.Infrastructure;

public sealed class TcpPlcGateway(PlcOptions options, ILogger<TcpPlcGateway> logger) : IPlcGateway
{
    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken token)
    {
        IsConnected = await ProbeAsync(token);
        if (!IsConnected) throw new IOException($"Passerelle automate TCP inaccessible ({options.Host}:{options.Port}).");
        logger.LogInformation("Passerelle automate TCP connectée sur {Host}:{Port}", options.Host, options.Port);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        logger.LogInformation("Passerelle automate TCP arrêtée");
        return Task.CompletedTask;
    }

    public async Task SendChuteAsync(string tag, int chute, int repeat, CancellationToken token)
    {
        if (!IsConnected) throw new InvalidOperationException("La connexion automate n'est pas démarrée.");
        for (var index = 0; index < Math.Clamp(repeat, 1, 3); index++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(options.Host, options.Port, token);
                await using var stream = client.GetStream();
                var payload = Encoding.UTF8.GetBytes($"p;{tag};{chute}");
                await stream.WriteAsync(payload, token);
                IsConnected = true;
                logger.LogInformation("Chute {Chute} envoyée au tag {Tag}", chute, tag);
            }
            catch
            {
                IsConnected = false;
                throw;
            }
        }
    }

    public async Task<bool> PingAsync(CancellationToken token)
    {
        if (!IsConnected) return false;
        IsConnected = await ProbeAsync(token);
        return IsConnected;
    }

    private async Task<bool> ProbeAsync(CancellationToken token)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(options.Host, options.Port, token);
            return true;
        }
        catch { return false; }
    }
}

public sealed class SimulationPlcGateway(ILogger<SimulationPlcGateway> logger) : IPlcGateway
{
    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken token)
    {
        IsConnected = true;
        logger.LogInformation("SIMULATION automate: connexion démarrée");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task SendChuteAsync(string tag, int chute, int repeat, CancellationToken token)
    {
        logger.LogInformation("SIMULATION automate: {Tag}={Chute} ({Repeat} fois)", tag, chute, repeat);
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(IsConnected);
}
