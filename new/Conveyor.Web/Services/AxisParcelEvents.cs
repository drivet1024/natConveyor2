using System.Threading.Channels;

namespace Conveyor.Web.Services;

public sealed record AxisParcelEvent(int LineId, long ParcelId, string Barcode, int Chute, DateTimeOffset ReadAt);

// Camera work cannot delay sorting, even if a disk or camera is unavailable.
public sealed class AxisParcelEvents
{
    private readonly Channel<AxisParcelEvent> _events = Channel.CreateBounded<AxisParcelEvent>(new BoundedChannelOptions(4096)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private long _dropped;
    public long Dropped => Interlocked.Read(ref _dropped);
    public void Publish(AxisParcelEvent parcel)
    {
        if (!_events.Writer.TryWrite(parcel)) Interlocked.Increment(ref _dropped);
    }
    public bool TryRead(out AxisParcelEvent? parcel) => _events.Reader.TryRead(out parcel);
}
