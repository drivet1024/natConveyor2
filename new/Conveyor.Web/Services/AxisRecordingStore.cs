using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Conveyor.Web.Options;
using Microsoft.Extensions.Options;

namespace Conveyor.Web.Services;

public sealed record AxisRecording(Guid Id, AxisParcelEvent Parcel, DateTimeOffset ArrivalAt,
    DateTimeOffset StartAt, DateTimeOffset EndAt, bool Incomplete, bool Simulated, long Bytes,
    IReadOnlyList<AxisFrameIndex> Frames)
{
    public string? VideoFileName { get; init; }
    public string CameraName { get; init; } = "Axis1";
}

public sealed class AxisRecordingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private DateTimeOffset _nextPrune;
    public string DirectoryPath { get; }
    public AxisRecordingStore(IWebHostEnvironment environment, IOptions<ConveyorOptions> options) : this(
        !string.IsNullOrWhiteSpace(options.Value.AxisCamera.RecordingDirectory)
            ? Path.GetFullPath(options.Value.AxisCamera.RecordingDirectory.Trim())
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Conveyor.Web",
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(environment.ContentRootPath).ToUpperInvariant())))[..16],
                "axis-recordings")) { }
    internal AxisRecordingStore(string path) => DirectoryPath = path;
    public string VideoPath(Guid id) => ReadManifest(id) is { } recording ? RecordingPath(recording) : Path.Combine(DirectoryPath, id.ToString("N") + ".avi");
    private string ManifestPath(Guid id) => Path.Combine(DirectoryPath, id.ToString("N") + ".json");

    private AxisRecording? ReadManifest(Guid id)
    {
        var path = ManifestPath(id);
        if (!File.Exists(path)) return null;
        try
        {
            var clip = JsonSerializer.Deserialize<AxisRecording>(File.ReadAllText(path), JsonOptions);
            return clip?.Id == id ? clip : null;
        }
        catch (JsonException) { return null; }
    }
    private string RecordingPath(AxisRecording recording)
    {
        var name = recording.VideoFileName ?? recording.Id.ToString("N") + ".avi";
        if (Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".avi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Nom de vidéo invalide.");
        return Path.Combine(DirectoryPath, name);
    }
    public AxisRecording? Find(Guid id)
    {
        var recording = ReadManifest(id);
        return recording is not null && File.Exists(RecordingPath(recording)) ? recording : null;
    }
    public IReadOnlyList<AxisRecording> Recent(int limit = 100)
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        return new DirectoryInfo(DirectoryPath).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out var id) ? Find(id) : null)
            .OfType<AxisRecording>().Take(limit).ToArray();
    }

    internal AxisClip Begin(AxisParcelEvent parcel, DateTimeOffset arrival, IReadOnlyList<AxisVideoFrame> buffer,
        AxisCameraOptions options, bool simulated)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (DateTimeOffset.UtcNow >= _nextPrune) Prune(options, DateTimeOffset.UtcNow);
        var frames = buffer.Where(frame => frame.At >= arrival.AddSeconds(-AxisCameraOptions.BeforeSeconds) && frame.At <= arrival.AddSeconds(AxisCameraOptions.AfterSeconds)).ToArray();
        if (frames.Length == 0) throw new InvalidOperationException("Aucune image disponible à l’arrivée estimée du colis.");
        if (!DirectoryPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DirectoryPath))!);
            if (drive.AvailableFreeSpace < 256L * 1024 * 1024) throw new IOException("Espace disque insuffisant pour les vidéos.");
        }
        var id = Guid.NewGuid();
        var partial = Path.Combine(DirectoryPath, id.ToString("N") + ".avi.partial");
        var clip = new AxisClip(this, id, partial, parcel, arrival, options.Name, simulated,
            new AxisAviWriter(partial, frames[0].Jpeg, options.FramesPerSecond));
        try { foreach (var frame in frames) clip.Append(frame); return clip; }
        catch { clip.Dispose(); throw; }
    }

    internal static string FileStem(string number, string camera, DateTimeOffset arrival) =>
        $"{SafePart(number)}_{SafePart(camera)}_{arrival.ToLocalTime():MM-dd_HH-mm}";
    private static string SafePart(string text)
    {
        var result = new string(text.Trim().Take(96).Select(value => char.IsLetterOrDigit(value) || value is '-' or '_' ? value : '_').ToArray());
        return string.IsNullOrWhiteSpace(result) ? "colis" : result;
    }

    internal void Save(AxisRecording recording, string partial)
    {
        var stem = FileStem(recording.Parcel.Barcode, recording.CameraName, recording.ArrivalAt);
        string destination;
        var duplicate = 1;
        while (true)
        {
            var name = stem + (duplicate == 1 ? "" : $"_{duplicate:00}") + ".avi";
            destination = Path.Combine(DirectoryPath, name);
            try { File.Move(partial, destination); recording = recording with { VideoFileName = name }; break; }
            catch (IOException) when (File.Exists(destination)) { duplicate++; }
        }
        var temporary = ManifestPath(recording.Id) + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(recording, JsonOptions));
            File.Move(temporary, ManifestPath(recording.Id), true);
        }
        catch { File.Delete(destination); File.Delete(temporary); throw; }
    }

    internal void Prune(AxisCameraOptions options, DateTimeOffset now)
    {
        _nextPrune = now.AddSeconds(10);
        if (!Directory.Exists(DirectoryPath)) return;
        var directory = new DirectoryInfo(DirectoryPath);
        // Abandoned writes from a crash are never offered as completed recordings.
        foreach (var abandoned in directory.EnumerateFiles("*.partial").Where(file => file.LastWriteTimeUtc < now.AddHours(-1).UtcDateTime))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(abandoned.Name)), "N", out _)) abandoned.Delete();
        // A configured folder may contain unrelated videos: only our indexed clips can be deleted.
        var files = directory.EnumerateFiles("*.json").Select(file => Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "N", out var id) ? Find(id) : null)
            .OfType<AxisRecording>().Select(clip => (Clip: clip, File: new FileInfo(RecordingPath(clip))))
            .OrderBy(item => item.File.LastWriteTimeUtc).ToArray();
        var bytes = files.Sum(item => item.File.Length) + directory.EnumerateFiles("*.avi.partial")
            .Where(file => Guid.TryParseExact(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file.Name)), "N", out _)).Sum(file => file.Length);
        foreach (var (clip, file) in files)
        {
            if (file.LastWriteTimeUtc >= now.AddDays(-options.RetentionDays).UtcDateTime && bytes <= options.MaximumStorageGb * 1024L * 1024 * 1024) continue;
            bytes -= file.Length; file.Delete(); File.Delete(ManifestPath(clip.Id));
        }
        if (bytes > options.MaximumStorageGb * 1024L * 1024 * 1024) throw new IOException("Limite de stockage vidéo atteinte.");
    }
}

internal sealed class AxisClip(AxisRecordingStore store, Guid id, string partial, AxisParcelEvent parcel,
    DateTimeOffset arrival, string cameraName, bool simulated, AxisAviWriter writer) : IDisposable
{
    public DateTimeOffset EndAt => arrival.AddSeconds(AxisCameraOptions.AfterSeconds);
    private bool _saved;
    public void Append(AxisVideoFrame frame)
    {
        if (frame.At <= EndAt && (writer.Frames.Count == 0 || frame.At > writer.Frames[^1].At)) writer.Append(frame);
    }
    public void Finish(bool interrupted = false)
    {
        var frames = writer.Frames.ToArray();
        if (frames.Length == 0) throw new InvalidOperationException("Vidéo sans images.");
        var incomplete = interrupted || frames[0].At > arrival.AddSeconds(-AxisCameraOptions.BeforeSeconds).AddMilliseconds(500) ||
            frames[^1].At < EndAt.AddMilliseconds(-500) ||
            frames.Zip(frames.Skip(1)).Any(pair => (pair.Second.At - pair.First.At).TotalSeconds > 1);
        writer.Complete(); writer.Dispose();
        store.Save(new(id, parcel, arrival, frames[0].At, frames[^1].At, incomplete, simulated, new FileInfo(partial).Length, frames)
            { CameraName = cameraName }, partial);
        _saved = true;
    }
    public void Dispose()
    {
        try { writer.Dispose(); } catch (IOException) { }
        if (!_saved)
        {
            // Cleanup may encounter antivirus locks or a revoked storage permission.
            // An abandoned partial is retried by retention; it must not stop the sorter.
            try { File.Delete(partial); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
