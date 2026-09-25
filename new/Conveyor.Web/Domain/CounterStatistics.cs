namespace Conveyor.Web.Domain;

public enum StatisticsDestination { ProductionLine, ProductionGlobal, Maintenance }

public sealed record CounterStatistics(int DepotId, int? LineId, DateTime ShiftStartedAt, long Scanned, long Rejected,
    long Recycled, long Sorted, double RejectedPercent, double RecycledPercent, double Code98Percent, double Code68Percent)
{
    public StatisticsDestination Destination { get; init; }
    public LineCounters? Counters { get; init; }
    public long WeightErrors => Counters?.ScaleErrors ?? 0;
    public long ScaleErrors => Counters?.ScaleFaults ?? 0;
    public long LightParcels => Counters?.LightParcels ?? 0;
    public long SmallParcels => Counters?.SmallParcels ?? 0;
    public long InverseLengthParcels => Counters?.InverseLengthParcels ?? 0;
    public double WeightErrorPercent => Percentage(WeightErrors, Scanned - (Counters?.NoReads ?? 0));
    public double ScaleErrorPercent => Percentage(ScaleErrors, Scanned);

    private static double Percentage(long count, long total) => total <= 0 ? 0 : Math.Round(100d * count / total, 2);

    public LineCounters RestoreCounters() => Counters?.Copy() ?? new()
    {
        TotalParcels = Scanned, CameraReads = Scanned, Rejected = Rejected, Code97 = Recycled,
        SortedByWaybill = Sorted,
        Code98 = (long)Math.Round(Scanned * Code98Percent / 100d),
        Code68 = (long)Math.Round(Scanned * Code68Percent / 100d)
    };

    public static CounterStatistics CaptureCombined(int depotId, DateTime shiftStartedAt,
        IEnumerable<LineCounters> lines)
    {
        var counters = lines.ToArray();
        var total = new LineCounters
        {
            TotalParcels = counters.Sum(line => line.TotalParcels),
            Rejected = counters.Sum(line => line.Rejected), Code97 = counters.Sum(line => line.Code97),
            Code98 = counters.Sum(line => line.Code98), Code68 = counters.Sum(line => line.Code68),
            NoReads = counters.Sum(line => line.NoReads),
            ScaleErrors = counters.Sum(line => line.ScaleErrors),
            ScaleFaults = counters.Sum(line => line.ScaleFaults),
            LightParcels = counters.Sum(line => line.LightParcels),
            SmallParcels = counters.Sum(line => line.SmallParcels),
            InverseLengthParcels = counters.Sum(line => line.InverseLengthParcels),
            NotInSystem = counters.Sum(line => line.NotInSystem),
            SortedWithoutIssue = counters.Sum(line => line.SortedWithoutIssue),
            SortedByWaybill = counters.Sum(line => line.SortedByWaybill),
            SortedByPostalCode = counters.Sum(line => line.SortedByPostalCode)
        };
        return Capture(depotId, 0, shiftStartedAt, total) with
        { Destination = StatisticsDestination.ProductionGlobal };
    }
    public static CounterStatistics Capture(int depotId, int? lineId, DateTime shiftStartedAt, LineCounters counters)
    {
        var scanned = counters.TotalParcels;
        double Percent(long count) => scanned == 0 ? 0 : Math.Round(100d * count / scanned, 2);
        return new CounterStatistics(depotId, lineId, shiftStartedAt, scanned, counters.Rejected, counters.Code97,
            counters.SortedByWaybill + counters.SortedByPostalCode,
            Percent(counters.Rejected), Percent(counters.Code97), Percent(counters.Code98), Percent(counters.Code68)) { Counters = counters.Copy() };
    }
}
