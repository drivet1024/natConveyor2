using System.Net;
using System.Threading.Channels;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Conveyor.Web.Services;

public sealed record AxisCameraStatus(bool Enabled, bool Connected, DateTimeOffset? LastImageAt,
    double BufferSeconds, int WaitingParcels, int RecordingClips, long MissedClips, string? Error)
{ public bool RecordingEnabled { get; init; } }

public sealed class AxisCameraService(IOptions<ConveyorOptions> configuration, ConveyorSupervisor supervisor,
    AxisParcelEvents events, AxisRecordingStore recordings, ILogger<AxisCameraService> logger) : BackgroundService
{
    private readonly AxisCameraOptions _options = configuration.Value.AxisCamera;
    private readonly object _gate = new();
    private readonly Channel<AxisVideoFrame> _frames = Channel.CreateBounded<AxisVideoFrame>(new BoundedChannelOptions(16)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private readonly Queue<AxisVideoFrame> _buffer = new();
    private readonly List<AxisClip> _clips = [];
    private readonly AxisArrivalPlanner _planner = new(DateTimeOffset.UtcNow);
    private AxisVideoFrame? _latest;
    private long _bufferBytes, _missed;
    private int _waiting, _recording;
    private string? _error;
    private string? _captureError;
    private DateTimeOffset _enabledSince = DateTimeOffset.MinValue;

    public bool NeedsRestart(AxisCameraOptions requested)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var before = JsonSerializer.SerializeToNode(_options, json)!.AsObject();
        var after = JsonSerializer.SerializeToNode(requested, json)!.AsObject();
        before.Remove("recordParcels"); after.Remove("recordParcels");
        return !JsonNode.DeepEquals(before, after);
    }
    public void SetRecordingEnabled(bool enabled)
    {
        lock (_gate)
        {
            _options.RecordParcels = enabled;
            if (!enabled) { _planner.Clear(); _waiting = 0; }
            else _enabledSince = DateTimeOffset.UtcNow;
        }
    }

    public AxisCameraStatus Status
    {
        get { lock (_gate) return new(_options.Enabled, _latest is { } image && image.At > DateTimeOffset.UtcNow.AddSeconds(-3),
            _latest?.At, _buffer.Count > 1 ? (_buffer.Last().At - _buffer.Peek().At).TotalSeconds : 0,
            _waiting, _recording, _missed + events.Dropped, _error ?? _captureError) { RecordingEnabled = _options.RecordParcels }; }
    }
    public AxisVideoFrame? Latest { get { lock (_gate) return _latest; } }
    private void MotionChanged() { lock (_gate) _planner.SetRunning(supervisor.ConveyorRunning == true, DateTimeOffset.UtcNow); }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (!_options.Enabled) return;
        if (_options.ValidationError() is { } error) { SetError(error); return; }
        supervisor.Changed += MotionChanged;
        MotionChanged();
        try { await Task.WhenAll(CaptureAsync(token), ProcessAsync(token)); }
        finally
        {
            supervisor.Changed -= MotionChanged;
            foreach (var clip in _clips)
            {
                try { clip.Finish(interrupted: true); } catch { }
                clip.Dispose();
            }
        }
    }

    private async Task CaptureAsync(CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, PreAuthenticate = false };
        var baseUri = new Uri(_options.BaseUrl.Trim().TrimEnd('/') + "/");
        var credentials = new CredentialCache();
        credentials.Add(baseUri, baseUri.Scheme == "https" ? "Basic" : "Digest", new NetworkCredential(_options.Username, _options.Password));
        handler.Credentials = credentials;
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var uri = new Uri(baseUri, $"axis-cgi/mjpg/video.cgi?camera={_options.VideoSource}&fps={_options.FramesPerSecond}&resolution={_options.Resolution}");
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var initial = CancellationTokenSource.CreateLinkedTokenSource(token);
                initial.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, initial.Token);
                if (!response.IsSuccessStatusCode) throw new HttpRequestException("Flux refusé", null, response.StatusCode);
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                await foreach (var jpeg in AxisMjpegReader.ReadAsync(stream, response.Content.Headers.ContentType?.ToString() ?? "", token))
                    _frames.Writer.TryWrite(new(DateTimeOffset.UtcNow, jpeg));
                SetCaptureError("Le flux de la caméra a été interrompu. Reconnexion en cours.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                SetCaptureError(exception is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
                    ? "Caméra : compte, mot de passe ou droits de lecture refusés."
                    : "Caméra inaccessible ou flux MJPEG incompatible. Reconnexion en cours.");
                logger.LogWarning("Connexion Axis interrompue ({ErrorType}).", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); } catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }

    private async Task ProcessAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var nextPrune = DateTimeOffset.UtcNow;
        while (await timer.WaitForNextTickAsync(token))
        {
            while (_frames.Reader.TryRead(out var frame))
            {
                lock (_gate)
                {
                    _latest = frame; _captureError = null; _buffer.Enqueue(frame); _bufferBytes += frame.Jpeg.Length;
                    while (_buffer.Count > 0 && (_buffer.Peek().At < frame.At.AddSeconds(-11) || _bufferBytes > 64L * 1024 * 1024))
                        _bufferBytes -= _buffer.Dequeue().Jpeg.Length;
                }
                foreach (var clip in _clips.ToArray())
                {
                    try { clip.Append(frame); }
                    catch { _clips.Remove(clip); clip.Dispose(); Missed("Écriture vidéo impossible. Vérifier l’espace disque et les droits du compte Windows."); }
                }
            }
            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<AxisArrival> arrivals;
            lock (_gate)
            {
                while (events.TryRead(out var parcel))
                {
                    try { if (parcel!.ReadAt >= _enabledSince) _planner.Schedule(parcel, _options); }
                    catch (InvalidOperationException) { _missed++; _error = "Trop de colis en attente de vidéo."; }
                }
                arrivals = _planner.Arrivals(now, _options); _waiting = _planner.Count;
            }
            foreach (var arrival in arrivals)
            {
                if (_clips.Count >= 16) { Missed("Limite de 16 vidéos simultanées atteinte."); continue; }
                try
                {
                    AxisVideoFrame[] buffer;
                    lock (_gate) buffer = _buffer.ToArray();
                    if (Latest is not { } latest || latest.At < now.AddSeconds(-3)) throw new InvalidOperationException("Pas d’image récente.");
                    _clips.Add(recordings.Begin(arrival.Parcel, arrival.At, buffer, _options, configuration.Value.Simulation));
                }
                catch { Missed("Vidéo manquée : caméra indisponible, espace disque insuffisant ou stockage inaccessible."); }
            }
            var recordingEnabled = Status.RecordingEnabled;
            foreach (var clip in _clips.Where(clip => !recordingEnabled || now >= clip.EndAt).ToArray())
            {
                _clips.Remove(clip);
                try { clip.Finish(interrupted: !recordingEnabled); lock (_gate) _error = null; }
                catch { Missed("Impossible de finaliser une vidéo. Vérifier le stockage."); }
                finally { clip.Dispose(); }
            }
            if (now >= nextPrune)
            {
                try { recordings.Prune(_options, now); }
                catch { SetError("Nettoyage du stockage vidéo impossible. Vérifier l’espace disque et les droits Windows."); }
                nextPrune = now.AddMinutes(1);
            }
            lock (_gate) _recording = _clips.Count;
        }
    }
    private void SetError(string error) { lock (_gate) _error = error; }
    private void SetCaptureError(string error) { lock (_gate) _captureError = error; }
    private void Missed(string error) { lock (_gate) { _missed++; _error = error; } logger.LogWarning("Axis : {Error}", error); }
}
