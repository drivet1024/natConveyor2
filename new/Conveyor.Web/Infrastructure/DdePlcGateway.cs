using Conveyor.Web.Options;
using Conveyor.Web.Services;

namespace Conveyor.Web.Infrastructure;

public sealed class DdePlcGateway : IPlcGateway, IPlcReadback, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _receptionGate = new();
    private readonly PlcOptions _options;
    private readonly ILogger<DdePlcGateway> _logger;
    private readonly Func<IDdeConnection> _createConnection;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, TagState> _tags;
    private IDdeConnection? _client;
    private Action<string, string>? _adviseHandler;
    private bool _connectionRequested;
    private bool _disposed;
    private volatile bool _readsHealthy;
    private DateTimeOffset _nextProbe;
    private int _probeIndex;
    private int _consecutiveReadFailures;
    public event Action<string, string>? TagChanged;

    public DdePlcGateway(PlcOptions options, ILogger<DdePlcGateway> logger, IEnumerable<string>? monitoredTags = null)
        : this(options, logger, monitoredTags, () => new DdeConnection(options.DdeService, options.DdeTopic), TimeProvider.System) { }

    internal DdePlcGateway(PlcOptions options, ILogger<DdePlcGateway> logger, IEnumerable<string>? monitoredTags,
        Func<IDdeConnection> createConnection, TimeProvider time)
    {
        _options = options;
        _logger = logger;
        _createConnection = createConnection;
        _time = time;
        _tags = (monitoredTags ?? [options.ChuteTag, options.TransferTag, options.CloseChute39Tag, ConveyorMotion.MotionTag])
            .Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(tag => tag, _ => new TagState(), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsConnected => _client?.IsConnected == true;
    // Keep transport state separate so a read fault does not prevent a stop command.
    public bool ReadsHealthy => IsConnected && _readsHealthy;

    public async Task ConnectAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("La connexion DDE exige Windows et RSLinx sur le même serveur.");
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connectionRequested = true;
            if (_client?.IsConnected != true) await OpenConnectionAsync(token);
        }
        finally { _gate.Release(); }
    }

    private async Task OpenConnectionAsync(CancellationToken token)
    {
        DisposeClient();
        token.ThrowIfCancellationRequested();
        var client = _createConnection();
        _client = client;
        _adviseHandler = (tag, value) => OnAdvise(client, tag, value);
        client.Advise += _adviseHandler;
        try
        {
            await Task.Run(client.Connect, token);
            _consecutiveReadFailures = 0;
            foreach (var tag in _tags.Keys) await SubscribeAsync(client, tag, false, token);
            _logger.LogInformation("Automate DDE connecté: service {Service}, sujet {Topic}", _options.DdeService, _options.DdeTopic);
        }
        catch { DisposeClient(); throw; }
    }

    private async Task SubscribeAsync(IDdeConnection client, string tag, bool restart, CancellationToken token)
    {
        lock (_receptionGate) { _tags[tag].Subscribed = false; UpdateReadHealth(); }
        try
        {
            if (restart) await Task.Run(() => client.StopAdvise(tag, 1_000), token);
            await Task.Run(() => client.StartAdvise(tag, 1_000), token);
            lock (_receptionGate) { _tags[tag].Subscribed = true; UpdateReadHealth(); }
            _logger.LogInformation("Surveillance DDE active pour le tag {Tag}", tag);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // StopAdvise may leave the old subscription alive. Recreate the
            // conversation instead of starting a duplicate loop in that case.
            if (restart) DisposeClient();
            _logger.LogWarning(exception, "Impossible de surveiller le tag DDE {Tag}; nouvelle tentative au prochain contrôle", tag);
        }
    }

    private void OnAdvise(IDdeConnection client, string tag, string value)
    {
        lock (_receptionGate)
        {
            if (!ReferenceEquals(client, _client) || !_tags.TryGetValue(tag, out var state)) return;
            state.Value = Normalize(value);
            state.Version++;
            state.ReadFailed = false;
            UpdateReadHealth();
            Publish(tag, state.Value);
        }
    }

    private void Publish(string tag, string value)
    {
        foreach (var subscriber in TagChanged?.GetInvocationList() ?? [])
        {
            try { ((Action<string, string>)subscriber)(tag, value); }
            catch (Exception exception) { _logger.LogError(exception, "Erreur de traitement de la réception DDE du tag {Tag}", tag); }
        }
    }

    public async Task<bool> PingAsync(CancellationToken token)
    {
        // Both lines call Ping. Probe one tag every five seconds and yield to
        // writes already in progress instead of queuing additional DDE traffic.
        if (!await _gate.WaitAsync(0, token)) return ReadsHealthy;
        try
        {
            if (_disposed || !_connectionRequested) return false;
            if (_time.GetUtcNow() < _nextProbe) return ReadsHealthy;
            _nextProbe = _time.GetUtcNow().AddSeconds(5);
            if (_client?.IsConnected != true)
            {
                _logger.LogWarning("Connexion DDE perdue ou indisponible; tentative de reconnexion à {Service}|{Topic}", _options.DdeService, _options.DdeTopic);
                await OpenConnectionAsync(token);
            }
            if (_tags.Count == 0) return ReadsHealthy;
            var client = _client!;
            var tag = _tags.Keys.ElementAt(_probeIndex);
            _probeIndex = (_probeIndex + 1) % _tags.Count;
            long version;
            lock (_receptionGate) version = _tags[tag].Version;
            string value;
            try { value = Normalize(await Task.Run(() => client.Request(tag, 1_000), token)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_receptionGate) { _tags[tag].ReadFailed = true; UpdateReadHealth(); }
                _logger.LogWarning(exception, "Lecture directe DDE échouée pour {Tag}; conversation connectée: {Connected}", tag, client.IsConnected);
                if (++_consecutiveReadFailures >= Math.Max(3, _tags.Count))
                {
                    _logger.LogWarning("Échecs consécutifs des lectures DDE; reconnexion au prochain contrôle");
                    DisposeClient();
                }
                return false;
            }
            _consecutiveReadFailures = 0;
            bool restart;
            bool subscribe;
            lock (_receptionGate)
            {
                var state = _tags[tag];
                if (state.ReadFailed) _logger.LogInformation("Lecture directe DDE rétablie pour {Tag}", tag);
                state.ReadFailed = false;
                // Never overwrite a newer notification delivered during Request.
                restart = state.Subscribed && state.Version == version && state.Value is not null && state.Value != value;
                subscribe = !state.Subscribed || restart;
                if (state.Version == version) { state.Value = value; Publish(tag, value); }
                UpdateReadHealth();
            }
            if (restart)
                _logger.LogWarning("Lecture directe DDE reçue pour {Tag} sans notification correspondante; reprise de l’abonnement (valeur {Value})", tag, value);
            if (subscribe) await SubscribeAsync(client, tag, restart, token);
            return ReadsHealthy;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _readsHealthy = false;
            _logger.LogWarning(exception, "Contrôle DDE échoué; nouvelle tentative au prochain contrôle");
            return false;
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _connectionRequested = false;
            DisposeClient();
            _logger.LogInformation("Connexion automate DDE arrêtée volontairement");
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
                throw new InvalidOperationException("La connexion DDE n'est pas disponible pour l’envoi.");
            for (var index = 0; index < Math.Clamp(repeat, 1, 3); index++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await Task.Run(() => client.Poke(tag, chute.ToString(), 3_000), token);
                    _logger.LogInformation("Chute {Chute} envoyée par DDE au tag {Tag}", chute, tag);
                }
                catch (Exception exception) when (exception is not OperationCanceledException && tag.StartsWith("COLISDDE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(exception, "Impossible d'envoyer la chute {Chute} par DDE au tag {Tag}; le traitement du colis se poursuit", chute, tag);
                }
            }
        }
        finally { _gate.Release(); }
    }

    private static string Normalize(string value) => value.TrimEnd('\0', '\r', '\n');
    private void UpdateReadHealth() => _readsHealthy = _tags.Values.All(state => state.Subscribed && !state.ReadFailed);

    private void DisposeClient()
    {
        IDdeConnection? client;
        lock (_receptionGate)
        {
            client = _client;
            _client = null;
            _readsHealthy = false;
            foreach (var state in _tags.Values) { state.Subscribed = false; state.ReadFailed = false; state.Value = null; state.Version = 0; }
        }
        if (client is null) return;
        client.Advise -= _adviseHandler;
        client.Dispose();
    }

    public void Dispose()
    {
        _gate.Wait();
        try { _disposed = true; _connectionRequested = false; DisposeClient(); }
        finally { _gate.Release(); }
    }

    private sealed class TagState
    {
        public string? Value;
        public long Version;
        public bool Subscribed;
        public bool ReadFailed;
    }
}
