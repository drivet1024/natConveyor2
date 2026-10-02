using System.Reflection;
using System.Text.Json;

namespace Conveyor.Web.Domain;

public sealed record SortingDayLine(int LineId, int? DatabaseLineId, bool Maintenance, LineCounters Counters)
{
    // Missing fields in older JSON must remain unknown rather than becoming historical zeros.
    public string[]? AvailableFields { get; init; }
}

public sealed record SortingDayChute(int Chute, long? FullTransitions, double? FullSeconds, bool? Full);
public sealed record SortingDayCadence(DateTimeOffset At, double ParcelsPerHour);

public sealed class SortingDaySnapshot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int DepotId { get; set; }
    public int ConveyorId { get; set; }
    public DateTime ShiftStart { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset Slot { get; set; }
    public string Session { get; set; } = "";
    public int ResetGeneration { get; set; }
    public string Source { get; set; } = "live";
    public int? SortingShiftId { get; set; }
    public bool? ConveyorRunning { get; set; }
    public int? FullChutesCount { get; set; }
    public int? Code42Count { get; set; }
    public List<SortingDayLine> Lines { get; set; } = [];
    public List<SortingDayChute> Chutes { get; set; } = [];
    public List<SortingDayCadence> Cadence { get; set; } = [];

    public static DateTimeOffset SlotFor(DateTimeOffset at) =>
        new(at.UtcTicks - at.UtcTicks % TimeSpan.FromMinutes(10).Ticks, TimeSpan.Zero);

    public static readonly PropertyInfo[] CounterFields = typeof(LineCounters).GetProperties()
        .Where(p => p.CanWrite && (p.PropertyType == typeof(long) || p.PropertyType == typeof(double)
            || p.PropertyType == typeof(decimal?))).ToArray();

    public static IReadOnlyDictionary<string, decimal?> Metrics(SortingDayLine line)
    {
        var available = line.AvailableFields?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = CounterFields.ToDictionary(p => p.Name, p =>
            available is not null && !available.Contains(p.Name) ? null :
            p.GetValue(line.Counters) is { } value ? (decimal?)Convert.ToDecimal(value) : null);
        decimal? Ratio(string numerator, string denominator) => values[numerator] is { } count && values[denominator] is { } total
            ? total > 0 ? count * 100m / total : 0 : null;
        values["SortedWithoutIssuePercent"] = Ratio(nameof(LineCounters.SortedWithoutIssue), nameof(LineCounters.TotalParcels));
        foreach (var field in new[] { "NoReads", "Code98", "Code68", "Code97", "RejectedShipmentNotFound", "RejectedRouteNotConfigured",
            "RejectedMultipleShipments", "RejectedCode86RetryLimit", "RejectedConfiguredRoute", "RejectedOther",
            "SmallParcels", "LightParcels", "InverseLengthParcels", "ScaleFaults" })
            values[field + "Percent"] = Ratio(field, nameof(LineCounters.TotalParcels));
        values["SortedParcels"] = values["SortedByWaybill"] + values["SortedByPostalCode"];
        values["TotalRejected"] = values["Rejected"] + values["RejectedShipmentNotFound"] + values["RejectedRouteNotConfigured"]
            + values["RejectedCode86RetryLimit"] + values["RejectedMultipleShipments"] + values["RejectedConfiguredRoute"] + values["RejectedOther"];
        values["SortedParcelsPercent"] = Ratio("SortedParcels", "TotalParcels");
        values["TotalRejectedPercent"] = Ratio("TotalRejected", "TotalParcels");
        foreach (var field in new[] { "ScaleErrors", "DimensionErrors" })
            values[field + "Percent"] = values[field] is { } count && values["TotalParcels"] is { } total && values["NoReads"] is { } noReads
                ? total > noReads ? count * 100m / (total - noReads) : 0 : null;
        values["ParcelsPerHour"] = values["TotalParcels"] is { } parcels && values["SortingRunSeconds"] is { } seconds
            ? seconds > 0 ? parcels * 3600m / seconds : 0 : null;
        values["AverageWeightPounds"] = values["MeasuredWeightPounds"] is { } weight && values["WeightMeasuredParcels"] is > 0
            ? weight / values["WeightMeasuredParcels"] : null;
        values["AverageVolumeCubicFeet"] = values["MeasuredVolumeCubicFeet"] is { } volume && values["VolumeMeasuredParcels"] is > 0
            ? volume / values["VolumeMeasuredParcels"] : null;
        return values;
    }

    public static SortingDayLine FromLegacy(int lineId, int? databaseLineId, bool maintenance, string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(lineId, databaseLineId, maintenance, JsonSerializer.Deserialize<LineCounters>(json)
            ?? throw new InvalidDataException("Compteurs historiques invalides."))
        { AvailableFields = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray() };
    }
}
