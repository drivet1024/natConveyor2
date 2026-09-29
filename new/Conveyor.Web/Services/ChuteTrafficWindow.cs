using Conveyor.Web.Domain;

namespace Conveyor.Web.Services;

// Accessed under the line controller's lock. A fallback replaces the same parcel's destination.
internal sealed class ChuteTrafficWindow
{
    private readonly Dictionary<string, PlcDispatch> _parcels = new();
    public void Add(PlcDispatch dispatch)
    {
        Prune(dispatch.SentAt);
        _parcels[dispatch.ParcelKey ?? dispatch.Sequence.ToString()] = dispatch;
    }
    public IReadOnlyDictionary<int, long> Counts(DateTimeOffset now)
    {
        Prune(now);
        return _parcels.Values.Where(p => p.SentAt <= now).GroupBy(p => p.Chute)
            .ToDictionary(g => g.Key, g => g.LongCount());
    }
    private void Prune(DateTimeOffset now)
    {
        var cutoff = now.AddMinutes(-15);
        foreach (var key in _parcels.Where(p => p.Value.SentAt <= cutoff).Select(p => p.Key).ToArray())
            _parcels.Remove(key);
    }
}
