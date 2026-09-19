using Conveyor.Web.Options;
using Conveyor.Web.Services;
using NDde.Client;

namespace Conveyor.Web.Infrastructure;

public sealed class DdePlcGateway(PlcOptions options, ILogger<DdePlcGateway> logger) : IPlcGateway, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DdeClient? _client;

    public bool IsConnected => _client?.IsConnected == true;

    public async Task ConnectAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("La connexion DDE exige Windows et RSLinx sur le même serveur.");

        await _gate.WaitAsync(token);
        try
        {
            if (IsConnected) return;
            DisposeClient();
            token.ThrowIfCancellationRequested();
            var client = new DdeClient(options.DdeService, options.DdeTopic);
            try
            {
                await Task.Run(client.Connect, token);
                _client = client;
                logger.LogInformation("Automate DDE connecté: service {Service}, sujet {Topic}", options.DdeService, options.DdeTopic);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_client is not null) logger.LogInformation("Connexion automate DDE arrêtée");
            DisposeClient();
        }
        finally { _gate.Release(); }
    }

    public async Task SendChuteAsync(string tag, int chute, int repeat, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var client = _client;
            if (client?.IsConnected != true)
                throw new InvalidOperationException("La connexion DDE n'est pas démarrée. Appuyez sur START pour la ligne principale.");

            for (var index = 0; index < Math.Clamp(repeat, 1, 3); index++)
            {
                token.ThrowIfCancellationRequested();
                await Task.Run(() => client.Poke(tag, chute.ToString(), 3_000), token);
                logger.LogInformation("Chute {Chute} envoyée par DDE au tag {Tag}", chute, tag);
            }
        }
        finally { _gate.Release(); }
    }

    public Task<bool> PingAsync(CancellationToken token) => Task.FromResult(IsConnected);

    private void DisposeClient()
    {
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        DisposeClient();
        _gate.Dispose();
    }
}
