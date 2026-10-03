using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace Conveyor.Web.Services;

internal static class AxisMjpegReader
{
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    public static async IAsyncEnumerable<byte[]> ReadAsync(Stream stream, string contentType,
        [EnumeratorCancellation] CancellationToken token)
    {
        var media = MediaTypeHeaderValue.Parse(contentType);
        if (!string.Equals(media.MediaType.Value, "multipart/x-mixed-replace", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Le flux Axis reçu n’est pas du MJPEG.");
        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128) throw new InvalidDataException("Délimiteur MJPEG invalide.");
        var reader = new MultipartReader(boundary, stream) { BodyLengthLimit = MaximumFrameBytes };
        while (true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var section = await reader.ReadNextSectionAsync(deadline.Token);
            if (section is null) yield break;
            await using var buffer = new MemoryStream();
            await section.Body.CopyToAsync(buffer, deadline.Token);
            var bytes = buffer.ToArray();
            if (bytes.Length < 4 || bytes[0] != 0xff || bytes[1] != 0xd8 || bytes[^2] != 0xff || bytes[^1] != 0xd9)
                throw new InvalidDataException("Image JPEG Axis invalide.");
            yield return bytes;
        }
    }
}
