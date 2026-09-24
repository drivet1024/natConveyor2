namespace Conveyor.Web.Options;

public sealed class StatisticsOptions
{
    public bool Enabled { get; set; }
    public TimeOnly ShiftStartTime { get; set; } = new(20, 0);
    // Kept for compatibility with existing settings; live persistence no longer uses it.
    public TimeOnly SaveTime { get; set; } = new(8, 25);

    public DateTime GetShiftStart(DateTime saveAt)
    {
        var start = saveAt.Date + ShiftStartTime.ToTimeSpan();
        return start > saveAt ? start.AddDays(-1) : start;
    }

    public string? ValidationError(IEnumerable<LineOptions> lines)
    {
        if (!Enabled) return null;
        var configuredLineIds = lines.Where(line => line.DatabaseLineId.HasValue)
            .Select(line => line.DatabaseLineId!.Value).ToArray();
        if (configuredLineIds.Any(lineId => lineId <= 0) ||
            configuredLineIds.Distinct().Count() != configuredLineIds.Length)
            return "Statistiques : les ID de ligne MySQL renseignés doivent être positifs et distincts.";
        return null;
    }
}
