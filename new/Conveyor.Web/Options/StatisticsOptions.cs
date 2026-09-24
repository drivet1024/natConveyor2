namespace Conveyor.Web.Options;

public sealed class StatisticsOptions
{
    public bool Enabled { get; set; }
    public TimeOnly ShiftStartTime { get; set; } = new(20, 0);
    public TimeOnly SaveTime { get; set; } = new(8, 25);

    public DateTime GetShiftStart(DateTime saveAt)
    {
        var start = saveAt.Date + ShiftStartTime.ToTimeSpan();
        return start >= saveAt ? start.AddDays(-1) : start;
    }

    public string? ValidationError(IEnumerable<LineOptions> lines)
    {
        if (!Enabled) return null;
        var configuredLines = lines.ToArray();
        if (configuredLines.Any(line => line.DatabaseLineId is null or <= 0) ||
            configuredLines.Select(line => line.DatabaseLineId).Distinct().Count() != configuredLines.Length)
            return "Statistiques : renseigner un ID de ligne MySQL positif et distinct pour chaque ligne utilisée.";
        if (ShiftStartTime == SaveTime) return "Statistiques : choisir une heure de sauvegarde différente du début du shift.";
        // All times belong to the same shift, which may cross midnight.
        double SinceStart(TimeOnly time) => (time.ToTimeSpan() - ShiftStartTime.ToTimeSpan() + TimeSpan.FromDays(1)).TotalSeconds % 86400;
        if (configuredLines.Any(line => SinceStart(SaveTime) > SinceStart(line.EndOfDay)))
            return "Statistiques : la sauvegarde doit précéder ou coïncider avec la remise à zéro quotidienne de chaque ligne, après le début du shift.";
        return null;
    }
}
