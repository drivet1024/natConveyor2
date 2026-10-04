using System.Threading.Channels;

namespace Conveyor.Web.Services;

public sealed record ParcelRecordingEvent(int LineId, long ParcelId, string Barcode, int Chute, DateTimeOffset ReadAt);

// Camera work cannot delay sorting, even if a disk or camera is unavailable.
public sealed class ParcelRecordingEvents
{
    private readonly Channel<ParcelRecordingEvent> _events = Channel.CreateBounded<ParcelRecordingEvent>(new BoundedChannelOptions(4096)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private long _dropped;
    public long Dropped => Interlocked.Read(ref _dropped);
    public void Publish(ParcelRecordingEvent parcel)
    {
        if (!_events.Writer.TryWrite(parcel)) Interlocked.Increment(ref _dropped);
    }
    public bool TryRead(out ParcelRecordingEvent? parcel) => _events.Reader.TryRead(out parcel);
}
