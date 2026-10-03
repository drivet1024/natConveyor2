using System.Text;

namespace Conveyor.Web.Services;

public sealed record AxisVideoFrame(DateTimeOffset At, byte[] Jpeg);
public sealed record AxisFrameIndex(DateTimeOffset At, long Offset, int Length);

// MJPEG is already compressed by the camera. An AVI container needs no encoder installation.
internal sealed class AxisAviWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly long _riffSize, _moviSize, _moviType, _totalFrames, _streamLength, _microseconds, _rate;
    private readonly List<AxisFrameIndex> _frames = [];
    private bool _complete;
    public IReadOnlyList<AxisFrameIndex> Frames => _frames;

    public AxisAviWriter(string path, byte[] firstFrame, int fps)
    {
        var (width, height) = Dimensions(firstFrame);
        _writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None), Encoding.ASCII);
        Four("RIFF"); _riffSize = Position; _writer.Write(0); Four("AVI ");
        var hdrl = List("hdrl");
        Four("avih"); _writer.Write(56);
        _microseconds = Position; _writer.Write(1_000_000 / fps);
        _writer.Write(0); _writer.Write(0); _writer.Write(0x10);
        _totalFrames = Position; _writer.Write(0);
        _writer.Write(0); _writer.Write(1); _writer.Write(0); _writer.Write(width); _writer.Write(height);
        for (var i = 0; i < 4; i++) _writer.Write(0);
        var strl = List("strl");
        Four("strh"); _writer.Write(56); Four("vids"); Four("MJPG");
        _writer.Write(0); _writer.Write((ushort)0); _writer.Write((ushort)0); _writer.Write(0);
        _writer.Write(1000); _rate = Position; _writer.Write(fps * 1000); _writer.Write(0);
        _streamLength = Position; _writer.Write(0);
        _writer.Write(0); _writer.Write(uint.MaxValue); _writer.Write(0);
        _writer.Write((short)0); _writer.Write((short)0); _writer.Write((short)width); _writer.Write((short)height);
        Four("strf"); _writer.Write(40); _writer.Write(40); _writer.Write(width); _writer.Write(height);
        _writer.Write((ushort)1); _writer.Write((ushort)24); Four("MJPG"); _writer.Write(width * height * 3);
        for (var i = 0; i < 4; i++) _writer.Write(0);
        EndList(strl); EndList(hdrl);
        _moviSize = List("movi"); _moviType = _moviSize + 4;
    }

    private long Position => _writer.BaseStream.Position;
    private void Four(string value) => _writer.Write(Encoding.ASCII.GetBytes(value));
    private long List(string type) { Four("LIST"); var size = Position; _writer.Write(0); Four(type); return size; }
    private void Patch(long position, int value) { var current = Position; _writer.BaseStream.Position = position; _writer.Write(value); _writer.BaseStream.Position = current; }
    private void EndList(long size) => Patch(size, checked((int)(Position - size - 4)));

    public void Append(AxisVideoFrame frame)
    {
        if (_complete) throw new InvalidOperationException("Vidéo déjà finalisée.");
        if (Position + frame.Jpeg.Length > 256L * 1024 * 1024) throw new IOException("Vidéo trop volumineuse.");
        Four("00dc"); _writer.Write(frame.Jpeg.Length);
        _frames.Add(new(frame.At, Position, frame.Jpeg.Length));
        _writer.Write(frame.Jpeg);
        if ((frame.Jpeg.Length & 1) != 0) _writer.Write((byte)0);
    }

    public void Complete()
    {
        if (_complete) return;
        EndList(_moviSize);
        Four("idx1"); _writer.Write(_frames.Count * 16);
        foreach (var frame in _frames)
        { Four("00dc"); _writer.Write(0x10); _writer.Write(checked((int)(frame.Offset - 8 - _moviType))); _writer.Write(frame.Length); }
        Patch(_totalFrames, _frames.Count); Patch(_streamLength, _frames.Count);
        if (_frames.Count > 1)
        {
            var seconds = (_frames[^1].At - _frames[0].At).TotalSeconds;
            if (seconds > 0)
            {
                var fps = (_frames.Count - 1) / seconds;
                Patch(_microseconds, (int)Math.Clamp(1_000_000 / fps, 1, int.MaxValue));
                Patch(_rate, (int)Math.Clamp(fps * 1000, 1, int.MaxValue));
            }
        }
        Patch(_riffSize, checked((int)(Position - 8))); _writer.Flush(); _complete = true;
    }

    internal static (int Width, int Height) Dimensions(byte[] jpeg)
    {
        for (var position = 2; position + 8 < jpeg.Length;)
        {
            if (jpeg[position++] != 0xff) throw new InvalidDataException("Structure JPEG invalide.");
            var marker = jpeg[position++];
            if (marker == 0xff) { position--; continue; }
            if (marker is 0xd9 or 0xda) break;
            if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
            var size = (jpeg[position] << 8) | jpeg[position + 1];
            if (size < 2 || position + size > jpeg.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                var height = (jpeg[position + 3] << 8) | jpeg[position + 4];
                var width = (jpeg[position + 5] << 8) | jpeg[position + 6];
                if (width > 0 && height > 0) return (width, height);
                break;
            }
            position += size;
        }
        throw new InvalidDataException("Dimensions JPEG introuvables.");
    }

    public void Dispose() => _writer.Dispose();
}
