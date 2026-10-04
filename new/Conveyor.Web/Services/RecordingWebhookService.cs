using System.Net.Http.Json;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

public sealed record RecordingWebhookStatus(bool Enabled, bool RecordingEnabled, int WaitingParcels,
    DateTimeOffset? LastSentAt, long Missed, string? Error);
public sealed record RecordingWebhookSimulation(string Barcode, DateTimeOffset ArrivalAt);

public sealed class RecordingWebhookService(IOptions<ConveyorOptions> configuration, ConveyorSupervisor supervisor,
    ParcelRecordingEvents events, ILogger<RecordingWebhookService>? logger = null) : BackgroundService
{
    private readonly RecordingWebhookOptions _options = configuration.Value.RecordingWebhook;
    private readonly object _gate = new();
    private readonly ParcelArrivalPlanner _planner = new(DateTimeOffset.UtcNow);
    private readonly List<ParcelArrival> _arrived = [];
    private readonly Guid _session = Guid.NewGuid();
    private long _sequence;
    private DateTimeOffset? _sent;
    private DateTimeOffset _enabledSince = DateTimeOffset.MinValue;
    private string? _error;
    private ParcelArrival? _simulation;
    public bool SimulationActive { get { lock (_gate) return _simulation is { } simulation && DateTimeOffset.UtcNow <= simulation.At.AddSeconds(5); } }
    public RecordingWebhookSimulation SimulateChute24() => SimulateChute24(DateTimeOffset.UtcNow);
    internal RecordingWebhookSimulation SimulateChute24(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_options.Enabled || !_options.RecordParcels) throw new InvalidOperationException("Activer le webhook et l’enregistrement dans la configuration avant le test.");
            if (_options.ValidationError() is { } error) throw new InvalidOperationException(error);
            if (!_options.TravelSeconds.ContainsKey(24)) throw new InvalidOperationException("Ajouter la chute 24 aux destinations du webhook avant le test.");
            if (_simulation is { } previous && now <= previous.At.AddSeconds(5)) throw new InvalidOperationException("Une simulation est déjà en cours.");
            var barcode = $"SIM-24-{now:yyyyMMdd-HHmmss-fff}";
            _simulation = new(new(_options.LineIds[0], -now.UtcTicks, barcode, 24, now), now.AddSeconds(15));
            return new(barcode, _simulation.At);
        }
    }
    public RecordingWebhookStatus Status { get { lock (_gate) return new(_options.Enabled, _options.RecordParcels,
        _planner.Count, _sent, events.Dropped, _error); } }
    public void SetRecordingEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_options.RecordParcels == enabled) return;
            _options.RecordParcels = enabled;
            _planner.Clear(); _arrived.Clear(); _simulation = null; _enabledSince = DateTimeOffset.UtcNow;
        }
    }
    private void MotionChanged() { lock (_gate) _planner.SetRunning(supervisor.ConveyorRunning == true, DateTimeOffset.UtcNow); }
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!_options.Enabled) return;
        if (_options.ValidationError() is { } error) { SetError(error); return; }
        supervisor.Changed += MotionChanged;
        MotionChanged();
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        client.DefaultRequestHeaders.Add("X-Conveyor-Key", _options.WebhookKey);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var message = NextMessage(DateTimeOffset.UtcNow);
                try
                {
                    using var response = await client.PostAsJsonAsync(_options.WebhookUrl, message, token);
                    response.EnsureSuccessStatusCode();
                    lock (_gate) { _sent = DateTimeOffset.UtcNow; _error = null; }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch { SetError("Récepteur indisponible : les vidéos peuvent être manquées. Nouvelle tentative automatique."); }
            }
        }
        finally { supervisor.Changed -= MotionChanged; }
    }
    private void SetError(string error)
    {
        lock (_gate)
        {
            if (_error == error) return;
            _error = error;
            logger?.LogWarning("Webhook enregistrement : {Error}", error);
        }
    }
    internal RecordingWebhookMessage NextMessage(DateTimeOffset now)
    {
        lock (_gate)
        {
            while (events.TryRead(out var parcel))
                if (parcel!.ReadAt >= _enabledSince)
                    try { _planner.Schedule(parcel, _options); }
                    catch (InvalidOperationException) { SetError("Trop de colis en attente d’enregistrement."); }
            if (_options.RecordParcels) _arrived.AddRange(_planner.Arrivals(now, _options));
            _arrived.RemoveAll(arrival => arrival.At.AddSeconds(5) < now);
            if (_simulation is { } expired && expired.At.AddSeconds(5) < now) _simulation = null;
            var jobs = _options.RecordParcels ? _planner.Upcoming(now, _options).Select(arrival => Job(arrival, false))
                .Concat(_arrived.Select(arrival => Job(arrival, true))).ToArray() : [];
            if (_options.RecordParcels && _simulation is { } simulation && simulation.At <= now.AddSeconds(12))
                jobs = [.. jobs, Job(simulation, now >= simulation.At) with { Simulated = true }];
            return new(1, _session, ++_sequence, now, _options.RecordParcels, jobs);
        }
    }
    private RecordingWebhookJob Job(ParcelArrival arrival, bool arrived) => new(
        $"{_session:N}-{arrival.Parcel.LineId}-{arrival.Parcel.ParcelId}-{arrival.Parcel.ReadAt.UtcTicks}",
        arrival.Parcel, _options.Name, arrival.At, arrived, configuration.Value.Simulation);
}
