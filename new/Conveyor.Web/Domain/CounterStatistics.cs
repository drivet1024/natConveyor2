namespace Conveyor.Web.Domain;

public enum StatisticsDestination { ProductionLine, ProductionGlobal, Maintenance }

public sealed record CounterStatistics(int DepotId, int LineId, DateTime ShiftStartedAt, long Scanned, long Rejected,
    long Recycled, long Sorted, double RejectedPercent, double RecycledPercent, double Code98Percent, double Code68Percent)
{
    public StatisticsDestination Destination { get; init; }

    public static CounterStatistics CaptureCombined(int depotId, DateTime shiftStartedAt,
        IEnumerable<LineCounters> lines)
    {
        var counters = lines.ToArray();
        var total = new LineCounters
        {
            TotalParcels = counters.Sum(line => line.TotalParcels),
            Rejected = counters.Sum(line => line.Rejected), Code97 = counters.Sum(line => line.Code97),
            Code98 = counters.Sum(line => line.Code98), Code68 = counters.Sum(line => line.Code68),
            SortedByWaybill = counters.Sum(line => line.SortedByWaybill),
            SortedByPostalCode = counters.Sum(line => line.SortedByPostalCode)
        };
        return Capture(depotId, 0, shiftStartedAt, total) with
        { Destination = StatisticsDestination.ProductionGlobal };
    }
    public static CounterStatistics Capture(int depotId, int lineId, DateTime shiftStartedAt, LineCounters counters)
    {
        var scanned = counters.TotalParcels;
        double Percent(long count) => scanned == 0 ? 0 : Math.Round(100d * count / scanned, 2);
        return new(depotId, lineId, shiftStartedAt, scanned, counters.Rejected, counters.Code97,
            counters.SortedByWaybill + counters.SortedByPostalCode,
            Percent(counters.Rejected), Percent(counters.Code97), Percent(counters.Code98), Percent(counters.Code68));
    }
}
