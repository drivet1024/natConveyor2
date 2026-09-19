using Conveyor.Web.Options;
using Conveyor.Web.Services;
using NDde.Client;

namespace Conveyor.Web.Infrastructure;

public sealed class DdePlcGateway(PlcOptions options, ILogger<DdePlcGateway> logger, IEnumerable<string>? monitoredTags = null) : IPlcGateway, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DdeClient? _client;
    public event Action<string, string>? TagChanged;

    private void OnAdvise(object? sender, DdeAdviseEventArgs args)
    {
        if (!ReferenceEquals(sender, _client)) return;
        TagChanged?.Invoke(args.Item, args.Text.TrimEnd('\0'));
    }

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
                client.Advise += OnAdvise;
                foreach (var tag in (monitoredTags ?? [options.ChuteTag]).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        await Task.Run(() => client.StartAdvise(tag, 1, true, 3_000), token);
                        logger.LogInformation("Surveillance DDE active pour le tag {Tag}", tag);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Impossible de surveiller le tag DDE {Tag}", tag);
                    }
                }
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
        if (_client is not null) _client.Advise -= OnAdvise;
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        DisposeClient();
        _gate.Dispose();
    }
}
