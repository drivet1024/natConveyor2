using System.Globalization;
using Conveyor.Web.Options;
using Conveyor.Web.Services;

namespace Conveyor.Web.Infrastructure;

public sealed class OpcDaPlcGateway : IPlcGateway, IPlcReadback, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _readGate = new();
    private readonly PlcOptions _options;
    private readonly ILogger<OpcDaPlcGateway> _logger;
    private readonly Func<IOpcDaConnection> _factory;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _lastReceived;
    private readonly HashSet<string> _badTags = new(StringComparer.OrdinalIgnoreCase);
    private IOpcDaConnection? _client;
    private Action<IReadOnlyList<OpcDaReading>>? _handler;
    private bool _requested, _disposed;
    private volatile bool _readsHealthy;
    private DateTimeOffset _nextProbe;
    public bool IsConnected => _client?.IsConnected == true;
    public bool ReadsHealthy => IsConnected && _readsHealthy;
    public event Action<string, string>? TagChanged;

    public OpcDaPlcGateway(PlcOptions options, ILogger<OpcDaPlcGateway> logger, IEnumerable<string> monitoredTags,
        IEnumerable<string>? subscriptionOnlyTags = null)
        : this(options, logger, monitoredTags.ToArray(), null, TimeProvider.System, subscriptionOnlyTags?.ToArray()) { }

    internal OpcDaPlcGateway(PlcOptions options, ILogger<OpcDaPlcGateway> logger, string[] tags,
        Func<IOpcDaConnection>? factory, TimeProvider time, string[]? subscriptionOnlyTags = null)
    {
        _options = options;
        _logger = logger;
        _time = time;
        var monitored = tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _lastReceived = monitored.ToDictionary(tag => tag, _ => DateTimeOffset.MinValue, StringComparer.OrdinalIgnoreCase);
        _factory = factory ?? (() => new OpcDaConnection(options, monitored, subscriptionOnlyTags));
    }

    public async Task ConnectAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _requested = true;
            if (!IsConnected) await OpenAsync(token);
        }
        finally { _gate.Release(); }
    }

    private async Task OpenAsync(CancellationToken token)
    {
        DisposeClient();
        token.ThrowIfCancellationRequested();
        var client = _factory();
        lock (_readGate) _client = client;
        _handler = values => Receive(client, values);
        client.ValuesChanged += _handler;
        try
        {
            await Task.Run(client.Connect, token);
            _logger.LogInformation("Automate OPC DA connecté : {Host} / {ProgId}, sujet {Topic}",
                string.IsNullOrWhiteSpace(_options.OpcHost) ? "local" : _options.OpcHost, _options.OpcProgId, _options.OpcTopic);
            await ReadAsync(client, token);
        }
        catch { DisposeClient(); throw; }
    }

    private async Task ReadAsync(IOpcDaConnection client, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var values = await Task.Run(() => client.ReadAsync(timeout.Token), token);
        Receive(client, values);
    }

    private void Receive(IOpcDaConnection client, IReadOnlyList<OpcDaReading> values)
    {
        lock (_readGate)
        {
            if (!ReferenceEquals(client, _client)) return;
            foreach (var reading in values)
            {
                if (!_lastReceived.TryGetValue(reading.Tag, out var previous) || reading.Timestamp < previous) continue;
                _lastReceived[reading.Tag] = reading.Timestamp;
                if (!reading.Good || reading.Value is null or Array)
                {
                    if (_badTags.Add(reading.Tag)) _logger.LogWarning("Lecture OPC DA invalide pour {Tag} : {Error}", reading.Tag, reading.Error);
                    continue;
                }
                if (_badTags.Remove(reading.Tag)) _logger.LogInformation("Lecture OPC DA rétablie pour {Tag}", reading.Tag);
                var value = reading.Value is bool flag ? (flag ? "1" : "0") : Convert.ToString(reading.Value, CultureInfo.InvariantCulture)!;
                foreach (var subscriber in TagChanged?.GetInvocationList() ?? [])
                {
                    try { ((Action<string, string>)subscriber)(reading.Tag, value); }
                    catch (Exception exception) { _logger.LogError(exception, "Erreur de traitement du tag OPC DA {Tag}", reading.Tag); }
                }
            }
            _readsHealthy = _badTags.Count == 0;
        }
    }

    public async Task<bool> PingAsync(CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return ReadsHealthy;
        try
        {
            if (!_requested || _disposed) return false;
            if (_time.GetUtcNow() < _nextProbe) return ReadsHealthy;
            _nextProbe = _time.GetUtcNow().AddSeconds(5);
            if (!IsConnected)
            {
                _logger.LogWarning("Connexion OPC DA indisponible ; tentative de reconnexion");
                await OpenAsync(token);
            }
            else await ReadAsync(_client!, token);
            return ReadsHealthy;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Contrôle OPC DA échoué ; reconnexion au prochain contrôle");
            DisposeClient();
            return false;
        }
        finally { _gate.Release(); }
    }

    public async Task SendChuteAsync(string tag, int chute, int repeat, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var client = _client;
            if (client?.IsConnected != true) throw new InvalidOperationException("La connexion OPC DA n’est pas disponible pour l’envoi.");
            for (var index = 0; index < Math.Clamp(repeat, 1, 3); index++)
            {
                token.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await Task.Run(() => client.WriteAsync(tag, chute, timeout.Token), token);
                _logger.LogInformation("Valeur {Value} envoyée par OPC DA au tag {Tag}", chute, tag);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync();
        try { _requested = false; DisposeClient(); }
        finally { _gate.Release(); }
    }

    private void DisposeClient()
    {
        IOpcDaConnection? client;
        lock (_readGate)
        {
            client = _client;
            _client = null;
            _readsHealthy = false;
            _badTags.Clear();
            foreach (var tag in _lastReceived.Keys) { _lastReceived[tag] = DateTimeOffset.MinValue; _badTags.Add(tag); }
        }
        if (client is null) return;
        client.ValuesChanged -= _handler;
        client.Dispose();
    }

    public void Dispose()
    {
        _gate.Wait();
        try { _disposed = true; _requested = false; DisposeClient(); }
        finally { _gate.Release(); }
    }
}
