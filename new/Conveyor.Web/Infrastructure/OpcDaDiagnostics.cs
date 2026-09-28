namespace Conveyor.Web.Infrastructure;

// Observation only: never reconnects, changes quality, or publishes a tag value.
internal sealed class OpcDaDiagnostics(ILogger logger, TimeProvider time, IEnumerable<string> polledTags,
    IEnumerable<string>? monitoredTags = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<string> _polled = polledTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TagState> _tags = (monitoredTags ?? polledTags)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(tag => tag, _ => new TagState(), StringComparer.OrdinalIgnoreCase);
    private ITimer? _timer;
    private DateTimeOffset _started, _lastControl, _nextControlWarning;
    private string? _operation;
    private DateTimeOffset _operationStarted;
    private int _skipped;
    private bool _controlStalled;
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(30);

    public void Start()
    {
        lock (_gate)
        {
            Stop();
            _started = _lastControl = time.GetUtcNow();
            _nextControlWarning = DateTimeOffset.MinValue;
            _controlStalled = false;
            _skipped = 0;
            foreach (var tag in _tags.Keys.ToArray()) _tags[tag] = new();
            _timer = time.CreateTimer(_ => Check(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    public void Operation(string? operation)
    {
        lock (_gate) { _operation = operation; _operationStarted = time.GetUtcNow(); }
    }

    public void Skipped()
    {
        lock (_gate) _skipped++;
    }

    public void ControlCompleted(DateTimeOffset started, int count)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            logger.LogDebug("Diagnostic OPC contrôle terminé : {StartedAt}, durée {DurationMs} ms, {Count} valeurs", started, (now - started).TotalMilliseconds, count);
            if (_controlStalled)
                logger.LogInformation("Diagnostic OPC contrôles repris après {SilenceSeconds} s ; {SkippedCount} contrôles sautés", (now - _lastControl).TotalSeconds, _skipped);
            _lastControl = now;
            _controlStalled = false;
            _skipped = 0;
        }
    }

    public void Received(OpcDaReading reading, string source, DateTimeOffset previous, bool accepted)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            // Also trace subscription-only tags when Debug is explicitly enabled.
            logger.LogDebug("Diagnostic OPC réception {Tag} : source {Source}, reçue {ReceivedAt}, horodatage OPC {OpcTimestamp}, précédent {PreviousOpcTimestamp}, valeur {Value}, qualité valide {Good}, détail {Quality}, acceptée {Accepted}",
                reading.Tag, source, now, reading.Timestamp, previous, reading.Value, reading.Good, reading.Error, accepted);
            if (!_tags.TryGetValue(reading.Tag, out var state)) return;
            state.LastArrival = now;
            state.Source = source;
            state.OpcTimestamp = reading.Timestamp;
            state.PreviousOpcTimestamp = previous;
            state.Value = reading.Value;
            state.Good = reading.Good;
            if (reading.Timestamp < previous) state.Rejected++;
            if (!accepted) return;
            if (state.Stalled)
                logger.LogInformation("Diagnostic OPC réception rétablie pour {Tag} après {SilenceSeconds} s : source {Source}, valeur {Value}, horodatage OPC {OpcTimestamp}, {RejectedCount} horodatages rejetés",
                    reading.Tag, (now - (state.LastAccepted ?? _started)).TotalSeconds, source, reading.Value, reading.Timestamp, state.Rejected);
            state.LastAccepted = now;
            state.Stalled = false;
            state.Rejected = 0;
        }
    }

    internal void Check()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            var now = time.GetUtcNow();
            if (now - _lastControl >= Threshold && now >= _nextControlWarning)
            {
                logger.LogWarning("Diagnostic OPC aucun contrôle terminé depuis {SilenceSeconds} s ; opération {Operation} depuis {OperationSeconds} s, {SkippedCount} contrôles sautés (verrou occupé)",
                    (now - _lastControl).TotalSeconds, _operation ?? "aucune", _operation is null ? 0 : (now - _operationStarted).TotalSeconds, _skipped);
                _controlStalled = true;
                _nextControlWarning = now + Threshold;
            }
            foreach (var (tag, state) in _tags)
            {
                // Subscription-only tags may legitimately stay unchanged. Alert only
                // when they actually deliver old timestamps that prevent acceptance.
                if (!_polled.Contains(tag) && state.Rejected == 0) continue;
                var silence = now - (state.LastAccepted ?? _started);
                if (silence < Threshold || now < state.NextWarning) continue;
                state.Stalled = true;
                state.NextWarning = now + Threshold;
                logger.LogWarning("Diagnostic OPC aucune réception acceptée pour {Tag} depuis {SilenceSeconds} s ; dernière arrivée {LastArrival}, source {Source}, horodatage OPC {OpcTimestamp}, précédent {PreviousOpcTimestamp}, valeur {Value}, qualité valide {Good}, {RejectedCount} horodatages rejetés",
                    tag, silence.TotalSeconds, state.LastArrival, state.Source, state.OpcTimestamp, state.PreviousOpcTimestamp, state.Value, state.Good, state.Rejected);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _operation = null;
        }
    }

    public void Dispose() => Stop();

    private sealed class TagState
    {
        public DateTimeOffset? LastArrival, LastAccepted, OpcTimestamp, PreviousOpcTimestamp;
        public DateTimeOffset NextWarning;
        public string? Source;
        public object? Value;
        public bool Good, Stalled;
        public int Rejected;
    }
}
