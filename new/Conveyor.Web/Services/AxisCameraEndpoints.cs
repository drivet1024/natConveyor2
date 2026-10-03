namespace Conveyor.Web.Services;

public static class AxisCameraEndpoints
{
    public static void MapAxisCamera(this WebApplication app)
    {
        app.MapGet("/api/axis/live", async (HttpContext context, AxisCameraService camera) =>
        {
            if (!camera.Status.Enabled) { context.Response.StatusCode = 503; return; }
            Multipart(context);
            DateTimeOffset? previous = null;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
            try
            {
                while (await timer.WaitForNextTickAsync(context.RequestAborted))
                {
                    var frame = camera.Latest;
                    if (frame is null || frame.At == previous || frame.At < DateTimeOffset.UtcNow.AddSeconds(-3)) continue;
                    await SendFrame(context, frame.Jpeg); previous = frame.At;
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        app.MapGet("/api/axis/recordings/{id:guid}/download", (Guid id, AxisRecordingStore store) =>
        {
            var clip = store.Find(id);
            return clip is null ? Results.NotFound() : Results.File(store.VideoPath(id), "video/x-msvideo",
                clip.VideoFileName ?? Path.GetFileName(store.VideoPath(id)), enableRangeProcessing: true);
        });
        app.MapGet("/api/axis/recordings/{id:guid}/replay", async (Guid id, HttpContext context, AxisRecordingStore store) =>
        {
            var recording = store.Find(id);
            if (recording is null) { context.Response.StatusCode = 404; return; }
            try
            {
                await using var stream = new FileStream(store.VideoPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                Multipart(context);
                DateTimeOffset? previous = null;
                foreach (var frame in recording.Frames)
                {
                    if (frame.Offset < 0 || frame.Length is < 4 or > AxisMjpegReader.MaximumFrameBytes || frame.Offset + frame.Length > stream.Length)
                        throw new InvalidDataException("Index vidéo invalide.");
                    if (previous is { } at) await Task.Delay(TimeSpan.FromSeconds(Math.Clamp((frame.At - at).TotalSeconds, 0, 30)), context.RequestAborted);
                    stream.Position = frame.Offset;
                    var image = new byte[frame.Length];
                    await stream.ReadExactlyAsync(image, context.RequestAborted);
                    await SendFrame(context, image); previous = frame.At;
                }
                await context.Response.WriteAsync("--conveyor-axis--\r\n", context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (FileNotFoundException) when (!context.Response.HasStarted) { context.Response.StatusCode = 404; }
        });
    }
    private static void Multipart(HttpContext context)
    {
        context.Response.ContentType = "multipart/x-mixed-replace; boundary=conveyor-axis";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }
    private static async Task SendFrame(HttpContext context, byte[] jpeg)
    {
        await context.Response.WriteAsync($"--conveyor-axis\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n", context.RequestAborted);
        await context.Response.Body.WriteAsync(jpeg, context.RequestAborted);
        await context.Response.WriteAsync("\r\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}
