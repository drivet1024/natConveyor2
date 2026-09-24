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
        var configuredLines = lines.ToArray();
        if (configuredLines.Any(line => line.DatabaseLineId is null or <= 0) ||
            configuredLines.Select(line => line.DatabaseLineId).Distinct().Count() != configuredLines.Length)
            return "Statistiques : renseigner un ID de ligne MySQL positif et distinct pour chaque ligne utilisée.";
        return null;
    }
}
