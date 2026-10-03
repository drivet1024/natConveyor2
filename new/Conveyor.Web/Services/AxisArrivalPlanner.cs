using Conveyor.Web.Options;

namespace Conveyor.Web.Services;

internal sealed record AxisArrival(AxisParcelEvent Parcel, DateTimeOffset At);

internal sealed class AxisArrivalPlanner(DateTimeOffset startedAt)
{
    private readonly List<(DateTimeOffset At, bool Running)> _motion = [(startedAt, false)];
    private readonly List<AxisParcelEvent> _pending = [];
    public int Count => _pending.Count;
    public void Clear() => _pending.Clear();

    public void SetRunning(bool running, DateTimeOffset at)
    {
        if (_motion[^1].Running == running) return;
        _motion.Add((at, running));
        // Retain enough motion history to cover delayed sorting and long stops.
        while (_motion.Count > 2 && _motion[1].At < at.AddDays(-1)) _motion.RemoveAt(0);
    }

    public bool Schedule(AxisParcelEvent parcel, AxisCameraOptions options)
    {
        if (!options.RecordParcels || !options.LineIds.Contains(parcel.LineId) || !options.TravelSeconds.ContainsKey(parcel.Chute)) return false;
        if (_pending.Count >= 4096) throw new InvalidOperationException("Trop de colis en attente de vidéo.");
        _pending.Add(parcel);
        return true;
    }

    public IReadOnlyList<AxisArrival> Arrivals(DateTimeOffset now, AxisCameraOptions options)
    {
        var arrived = _pending.Select(parcel => (Parcel: parcel, At: ArrivalTime(parcel.ReadAt, options.TravelSeconds[parcel.Chute], now)))
            .Where(item => item.At.HasValue).Select(item => new AxisArrival(item.Parcel, item.At!.Value)).ToArray();
        foreach (var arrival in arrived) _pending.Remove(arrival.Parcel);
        _pending.RemoveAll(parcel => parcel.ReadAt < now.AddDays(-1));
        return arrived;
    }

    private DateTimeOffset? ArrivalTime(DateTimeOffset start, double remaining, DateTimeOffset now)
    {
        foreach (var (point, i) in _motion.Select((point, i) => (point, i)))
        {
            var from = point.At > start ? point.At : start;
            var to = i + 1 < _motion.Count && _motion[i + 1].At < now ? _motion[i + 1].At : now;
            if (!point.Running || to <= from) continue;
            var seconds = (to - from).TotalSeconds;
            if (seconds >= remaining) return from.AddSeconds(remaining);
            remaining -= seconds;
        }
        return null;
    }

    internal double RunningSeconds(DateTimeOffset start, DateTimeOffset end)
    {
        double seconds = 0;
        for (var i = 0; i < _motion.Count; i++)
        {
            var from = _motion[i].At > start ? _motion[i].At : start;
            var to = i + 1 < _motion.Count && _motion[i + 1].At < end ? _motion[i + 1].At : end;
            if (_motion[i].Running && to > from) seconds += (to - from).TotalSeconds;
        }
        return seconds;
    }
}
