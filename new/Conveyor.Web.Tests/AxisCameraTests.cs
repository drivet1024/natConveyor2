using Conveyor.Web.Options;
using Conveyor.Web.Services;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Conveyor.Web.Infrastructure;

namespace Conveyor.Web.Tests;

public sealed class AxisCameraTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static byte[] Image()
    {
        using var resource = typeof(AxisCameraTests).Assembly.GetManifestResourceStream("Conveyor.Web.Tests.Fixtures.axis-test-frame.jpg")!;
        using var result = new MemoryStream(); resource.CopyTo(result); return result.ToArray();
    }

    [Fact]
    public async Task BackgroundCaptureRecordsCompleteWindowWithoutAnOpenBrowser()
    {
        var directory = Path.Combine(Path.GetTempPath(), "conveyor-axis-live-test-" + Guid.NewGuid());
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        var jpeg = Image();
        server.MapGet("/axis-cgi/mjpg/video.cgi", async (HttpContext context) =>
        {
            Assert.False(context.Request.Query.ContainsKey("password"));
            context.Response.ContentType = "multipart/x-mixed-replace; boundary=axis";
            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    await context.Response.WriteAsync($"--axis\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n", context.RequestAborted);
                    await context.Response.Body.WriteAsync(jpeg, context.RequestAborted);
                    await context.Response.WriteAsync("\r\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    await Task.Delay(100, context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        await server.StartAsync();
        var options = new ConveyorOptions { Simulation = true, Lines = [new() { Id = 0 }],
            AxisCamera = new() { Enabled = true, Username = "test-viewer", Password = "test-only",
                BaseUrl = server.Urls.Single(), RecordingDirectory = directory, TravelSeconds = new() { [26] = 1 } } };
        options.ApplyGlobalSorting();
        var settings = Microsoft.Extensions.Options.Options.Create(options);
        var events = new AxisParcelEvents();
        var repository = new SimulationConveyorRepository();
        using var supervisor = new ConveyorSupervisor(settings, repository, new SortEngine(repository, NullLogger<SortEngine>.Instance),
            NullLoggerFactory.Instance, new TestConfigurationEditor(), axisEvents: events);
        var store = new AxisRecordingStore(directory);
        using var camera = new AxisCameraService(settings, supervisor, events, store, NullLogger<AxisCameraService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        try
        {
            await camera.StartAsync(CancellationToken.None);
            await Until(() => camera.Status.BufferSeconds >= 10, deadline.Token);
            supervisor.RecordPlcTagChange("DEPART_SYSTEMES", "1");
            var reading = DateTimeOffset.UtcNow;
            events.Publish(new(0, 7, "CAMERA-TEST", 26, reading));
            await Until(() => store.Recent().Count > 0, deadline.Token);
            var saved = Assert.Single(store.Recent());
            Assert.False(saved.Incomplete);
            Assert.True(saved.Simulated);
            Assert.Equal(26, saved.Parcel.Chute);
            Assert.InRange((saved.StartAt - saved.ArrivalAt).TotalSeconds, -10.1, -9.5);
            Assert.InRange((saved.EndAt - saved.ArrivalAt).TotalSeconds, 4.5, 5.1);
            Assert.Equal(0, camera.Status.MissedClips);
            var toggle = new AxisCameraOptions { Enabled = true, Username = "test-viewer", Password = "test-only",
                BaseUrl = server.Urls.Single(), RecordingDirectory = directory, TravelSeconds = new() { [26] = 1 }, RecordParcels = false };
            Assert.False(camera.NeedsRestart(toggle));
            camera.SetRecordingEnabled(false);
            events.Publish(new(0, 8, "DISABLED", 26, DateTimeOffset.UtcNow));
            await Task.Delay(1200, deadline.Token);
            Assert.Single(store.Recent());
            Assert.False(camera.Status.RecordingEnabled);
            Assert.True(camera.Status.Connected);
            camera.SetRecordingEnabled(true);
            events.Publish(new(0, 9, "ENABLED", 26, DateTimeOffset.UtcNow));
            await Until(() => store.Recent().Count == 2, deadline.Token);
            Assert.True(camera.Status.RecordingEnabled);
            Assert.All(store.Recent(), clip => Assert.False(clip.Incomplete));
        }
        finally
        {
            await camera.StopAsync(CancellationToken.None);
            await server.StopAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
    private static async Task Until(Func<bool> condition, CancellationToken token)
    { while (!condition()) await Task.Delay(100, token); }

    [Fact]
    public void DefaultCameraIsDisabledWithRequestedAddressAndThreeChutes()
    {
        var options = new AxisCameraOptions();
        Assert.False(options.Enabled);
        Assert.Equal("http://10.11.5.6", options.BaseUrl);
        Assert.Equal(new[] { 24, 25, 26 }, options.TravelSeconds.Keys.Order());
        Assert.Null(options.ValidationError());
        options.Enabled = true;
        Assert.NotNull(options.ValidationError());
    }

    [Theory]
    [InlineData("http://user:secret@10.11.5.6")]
    [InlineData("file:///tmp/test")]
    [InlineData("http://10.11.5.6/other")]
    [InlineData("http://10.11.5.6?password=secret")]
    public void CredentialsAndArbitraryPathsCannotBePlacedInCameraUrl(string address) =>
        Assert.NotNull(new AxisCameraOptions { BaseUrl = address }.ValidationError());

    [Fact]
    public void ArrivalUsesReadingTimeAndPausesForStopsAndUnknownMotion()
    {
        var options = new AxisCameraOptions { TravelSeconds = new() { [24] = 90 } };
        var planner = new AxisArrivalPlanner(Epoch);
        planner.SetRunning(true, Epoch);
        var parcel = new AxisParcelEvent(0, 1, "ABC", 24, Epoch);
        Assert.True(planner.Schedule(parcel, options));
        planner.SetRunning(false, Epoch.AddSeconds(30));
        Assert.Empty(planner.Arrivals(Epoch.AddSeconds(100), options));
        planner.SetRunning(true, Epoch.AddSeconds(100));
        Assert.Empty(planner.Arrivals(Epoch.AddSeconds(159), options));
        var arrival = Assert.Single(planner.Arrivals(Epoch.AddSeconds(160), options));
        Assert.Equal(parcel, arrival.Parcel);
        Assert.Equal(Epoch.AddSeconds(160), arrival.At);
        Assert.Empty(planner.Arrivals(Epoch.AddSeconds(200), options));
    }

    [Fact]
    public void FinalDestinationAndReadingStationFilterRecordingsAndSupportOverlappingParcels()
    {
        var options = new AxisCameraOptions { LineIds = [0], TravelSeconds = new() { [26] = 20 } };
        var planner = new AxisArrivalPlanner(Epoch);
        planner.SetRunning(true, Epoch);
        Assert.False(planner.Schedule(new(0, 1, "ABC", 98, Epoch), options));
        Assert.False(planner.Schedule(new(1, 2, "ABC", 26, Epoch), options));
        Assert.True(planner.Schedule(new(0, 3, "ABC", 26, Epoch), options));
        Assert.True(planner.Schedule(new(0, 4, "ABC", 26, Epoch), options));
        Assert.Equal(2, planner.Arrivals(Epoch.AddSeconds(20), options).Count);
        options.RecordParcels = false;
        Assert.False(planner.Schedule(new(0, 5, "ABC", 26, Epoch), options));
    }

    [Fact]
    public void DelayedSortingUsesMotionHistoryInsteadOfAssumingContinuousMovement()
    {
        var options = new AxisCameraOptions { TravelSeconds = new() { [24] = 15 } };
        var planner = new AxisArrivalPlanner(Epoch);
        planner.SetRunning(true, Epoch);
        planner.SetRunning(false, Epoch.AddSeconds(5));
        planner.SetRunning(true, Epoch.AddSeconds(20));
        planner.Schedule(new(0, 1, "ABC", 24, Epoch), options);
        Assert.Empty(planner.Arrivals(Epoch.AddSeconds(29), options));
        Assert.Single(planner.Arrivals(Epoch.AddSeconds(30), options));
    }

    [Fact]
    public async Task AxisMjpegReaderAcceptsFragmentedMultipartJpegData()
    {
        var jpeg = Image();
        using var payload = new MemoryStream();
        payload.Write(Encoding.ASCII.GetBytes($"--axis\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n"));
        payload.Write(jpeg); payload.Write(Encoding.ASCII.GetBytes("\r\n--axis--\r\n"));
        using var stream = new FragmentedStream(payload.ToArray());
        var frames = new List<byte[]>();
        await foreach (var frame in AxisMjpegReader.ReadAsync(stream, "multipart/x-mixed-replace; boundary=\"axis\"", CancellationToken.None)) frames.Add(frame);
        Assert.Equal(jpeg, Assert.Single(frames));
    }

    [Fact]
    public async Task AxisMjpegReaderRejectsUnexpectedContentAndNonJpegFrames()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("--axis\r\nContent-Type: image/jpeg\r\n\r\nBAD DATA\r\n--axis--\r\n"));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var _ in AxisMjpegReader.ReadAsync(stream, "multipart/x-mixed-replace; boundary=axis", CancellationToken.None)) { } });
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var _ in AxisMjpegReader.ReadAsync(stream, "text/html", CancellationToken.None)) { } });
    }

    [Fact]
    public void ClipContainsTenSecondsBeforeArrivalAndFiveAfterAndHasPlayableAviIndex()
    {
        var directory = Path.Combine(Path.GetTempPath(), "conveyor-axis-test-" + Guid.NewGuid());
        try
        {
            var store = new AxisRecordingStore(directory);
            var image = Image();
            Assert.Equal((640, 480), AxisAviWriter.Dimensions(image));
            var buffer = Enumerable.Range(-110, 111).Select(i => new AxisVideoFrame(Epoch.AddMilliseconds(i * 100), image)).ToArray();
            using var clip = store.Begin(new(0, 1, "PARCEL", 24, Epoch.AddSeconds(-96.6)), Epoch, buffer, new(), false);
            foreach (var i in Enumerable.Range(1, 60)) clip.Append(new(Epoch.AddMilliseconds(i * 100), image));
            clip.Finish();
            var saved = Assert.Single(store.Recent());
            Assert.False(saved.Incomplete);
            Assert.Equal(Epoch.AddSeconds(-10), saved.StartAt);
            Assert.Equal(Epoch.AddSeconds(5), saved.EndAt);
            Assert.Equal(151, saved.Frames.Count);
            using var reader = new BinaryReader(File.OpenRead(store.VideoPath(saved.Id)));
            Assert.Equal("RIFF", Encoding.ASCII.GetString(reader.ReadBytes(4)));
            Assert.Equal(reader.BaseStream.Length - 8, reader.ReadInt32());
            Assert.Equal("AVI ", Encoding.ASCII.GetString(reader.ReadBytes(4)));
            foreach (var frame in saved.Frames)
            {
                reader.BaseStream.Position = frame.Offset - 8;
                Assert.Equal("00dc", Encoding.ASCII.GetString(reader.ReadBytes(4)));
                Assert.Equal(image.Length, reader.ReadInt32()); Assert.Equal(image, reader.ReadBytes(frame.Length));
            }
            // Optional local artifact for independent validation with a media player.
            if (Environment.GetEnvironmentVariable("AXIS_VIDEO_TEST_OUTPUT") is { Length: > 0 } output)
            { Directory.CreateDirectory(output); File.Copy(store.VideoPath(saved.Id), Path.Combine(output, "axis-test.avi"), true); }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void MissingPrebufferOrPostbufferIsExplicitlyMarkedIncompleteAndRetentionRemovesExpiredClips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "conveyor-axis-test-" + Guid.NewGuid());
        try
        {
            var store = new AxisRecordingStore(directory);
            var image = Image();
            using (var clip = store.Begin(new(1, 2, "TEST", 26, Epoch.AddSeconds(-90)), Epoch,
                [new(Epoch.AddSeconds(-2), image), new(Epoch, image)], new(), true)) clip.Finish(interrupted: true);
            var recording = Assert.Single(store.Recent());
            Assert.True(recording.Incomplete); Assert.True(recording.Simulated);
            File.SetLastWriteTimeUtc(store.VideoPath(recording.Id), DateTime.UtcNow.AddDays(-8));
            store.Prune(new(), DateTimeOffset.UtcNow);
            Assert.Empty(store.Recent()); Assert.Null(store.Find(recording.Id));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void NamesIncludeParcelCameraDateAndTimeAndDuplicateVideosArePreserved()
    {
        var directory = Path.Combine(Path.GetTempPath(), "conveyor-axis-names-" + Guid.NewGuid());
        try
        {
            var store = new AxisRecordingStore(directory);
            var options = new AxisCameraOptions { Name = "Cam/1" };
            for (var i = 0; i < 2; i++)
            {
                using var clip = store.Begin(new(0, i, "12345678901", 24, Epoch), Epoch, [new(Epoch, Image())], options, false);
                clip.Finish(true);
            }
            var stem = $"12345678901_Cam_1_{Epoch.ToLocalTime():MM-dd_HH-mm}";
            Assert.Equal(new[] { stem + ".avi", stem + "_02.avi" }, store.Recent().Select(clip => clip.VideoFileName).Order(StringComparer.Ordinal));
            var unrelated = Path.Combine(directory, "other.avi");
            File.WriteAllText(unrelated, "keep");
            File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));
            store.Prune(options, DateTimeOffset.UtcNow.AddDays(10));
            Assert.Equal("keep", File.ReadAllText(unrelated));
            Assert.Empty(store.Recent());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void RecordingRequiresAnAbsoluteFolderWhileLiveOnlyDoesNot()
    {
        var options = new AxisCameraOptions { Enabled = true, Username = "viewer", Password = "test", RecordParcels = false };
        Assert.Null(options.ValidationError());
        options.RecordParcels = true;
        Assert.NotNull(options.ValidationError());
        options.RecordingDirectory = "relative";
        Assert.NotNull(options.ValidationError());
        options.RecordingDirectory = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.NotNull(options.ValidationError());
        options.RecordingDirectory = Path.Combine(Path.GetTempPath(), "axis");
        Assert.Null(options.ValidationError());
    }

    [Fact]
    public async Task CameraConfigurationPatchPreservesOtherSavedSettingsAndPassword()
    {
        var directory = Path.Combine(Path.GetTempPath(), "conveyor-axis-config-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ConveyorOptions();
            var editor = new ConfigurationEditor(Microsoft.Extensions.Options.Options.Create(options), new TestEnvironment { ContentRootPath = directory });
            await File.WriteAllTextAsync(editor.FilePath, "{\"Conveyor\":{\"General\":{\"ShiftId\":42},\"Database\":{\"ConnectionString\":\"keep\"}}}");
            await editor.SaveAxisCameraAsync(new() { Enabled = true, Username = "viewer", Password = "test-only", RecordingDirectory = directory });
            var restored = new ConfigurationBuilder().AddJsonFile(editor.FilePath).Build().GetSection("Conveyor").Get<ConveyorOptions>()!;
            Assert.Equal(42, restored.General!.ShiftId); Assert.Equal("keep", restored.Database.ConnectionString);
            Assert.Equal("test-only", restored.AxisCamera.Password); Assert.True(restored.AxisCamera.Enabled);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 7)], cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => base.ReadAsync(buffer, offset, Math.Min(count, 7), cancellationToken);
    }
    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
