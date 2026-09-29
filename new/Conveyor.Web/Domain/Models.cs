namespace Conveyor.Web.Domain;

public sealed record Dimension(decimal Length, decimal Width, decimal Height, int Status = 0)
{
    public static readonly Dimension Missing = new(-1, -1, -1, -1);
    public bool IsValid(decimal maximum) => Length > 0 && Width > 0 && Height > 0 &&
                                             Length <= maximum && Width <= maximum && Height <= maximum;
}

public sealed record TimedValue<T>(T Value, DateTimeOffset Timestamp);
public sealed record ConveyorShift(int Id, string Name);

public sealed record Shipment(string ShippingId, int CustomerId, int RouteId, bool DisableCode98, string? DestinationPostalCode = null);

public sealed record ParcelContext(
    string CameraData,
    DateTimeOffset CameraTimestamp,
    Dimension Dimension,
    DateTimeOffset? DimensionTimestamp,
    decimal Weight,
    DateTimeOffset? WeightTimestamp,
    long ParcelId = 0);

public sealed record SortDecision(
    string Barcode,
    string PostalCode,
    int Chute,
    int PlcChute,
    string Reason,
    Dimension Dimension,
    decimal Weight,
    DateTimeOffset Timestamp,
    string? DestinationPostalCode = null,
    int? RouteId = null,
    bool? DisableCode98 = null,
    bool ShipmentNotFound = false,
    int? Code98PassCount = null,
    bool CountShipmentNotFound = false,
    bool CountNoRead = false,
    string? Waybill = null,
    MissingShipmentAttribution? MissingShipmentCustomer = null);

public sealed record ShipmentCustomerPrefix(int? CustomerId, string Prefix);
public sealed record MissingShipmentAttribution(string Customers, string Prefixes);
public sealed record MissingShipmentCustomerCount(string Customers, string Prefixes, long Count);

public sealed record UnconfiguredRouteParcel(string Waybill, string? PostalCode, int? RouteId, DateTimeOffset ReceivedAt);
public sealed record MultipleBarcodeParcel(string CameraData, DateTimeOffset ReceivedAt);
public sealed record SmallParcelDetail(decimal Weight, Dimension Dimension, DateTimeOffset ReceivedAt);

public sealed class LineCounters
{
    public LineCounters Copy()
    {
        var copy = (LineCounters)MemberwiseClone();
        copy.UnconfiguredRouteParcels = UnconfiguredRouteParcels.ToArray();
        copy.MultipleBarcodeParcels = MultipleBarcodeParcels.ToArray();
        copy.SmallParcelDetails = SmallParcelDetails.ToArray();
        copy.MissingShipmentCustomers = MissingShipmentCustomers.ToArray();
        copy.ChuteDispatchCounts = new Dictionary<int, long>(ChuteDispatchCounts);
        return copy;
    }
    public IReadOnlyList<UnconfiguredRouteParcel> UnconfiguredRouteParcels { get; set; } = [];
    public IReadOnlyList<MissingShipmentCustomerCount> MissingShipmentCustomers { get; set; } = [];
    public void RecordMissingShipmentCustomer(MissingShipmentAttribution? attribution)
    {
        if (attribution is null) return;
        var rows = MissingShipmentCustomers.ToList();
        var index = rows.FindIndex(row => row.Customers == attribution.Customers);
        if (index < 0) rows.Add(new(attribution.Customers, attribution.Prefixes, 1));
        else rows[index] = rows[index] with
        {
            Count = rows[index].Count + 1,
            Prefixes = string.Join(", ", rows[index].Prefixes.Split(", ").Concat(attribution.Prefixes.Split(", ")).Distinct().Order())
        };
        MissingShipmentCustomers = rows.ToArray();
    }
    public IReadOnlyList<MultipleBarcodeParcel> MultipleBarcodeParcels { get; set; } = [];
    public IReadOnlyList<SmallParcelDetail> SmallParcelDetails { get; set; } = [];
    public void RecordSmallParcel(ParcelContext parcel)
    {
        SmallParcelDetails = SmallParcelDetails.TakeLast(499)
            .Append(new SmallParcelDetail(parcel.Weight, parcel.Dimension, parcel.CameraTimestamp)).ToArray();
    }
    public void RecordMultipleBarcodes(ParcelContext parcel)
    {
        MultipleBarcodeParcels = MultipleBarcodeParcels.TakeLast(499)
            .Append(new MultipleBarcodeParcel(parcel.CameraData, parcel.CameraTimestamp)).ToArray();
    }
    public void RecordUnconfiguredRoute(SortDecision decision)
    {
        UnconfiguredRouteParcels = UnconfiguredRouteParcels.TakeLast(499)
            .Append(new UnconfiguredRouteParcel(decision.Waybill ?? decision.Barcode,
                string.IsNullOrWhiteSpace(decision.DestinationPostalCode) ? decision.PostalCode : decision.DestinationPostalCode,
                decision.RouteId, decision.Timestamp)).ToArray();
    }
    public long CameraReads { get; set; }
    public long DimensionReads { get; set; }
    public long ScaleReads { get; set; }
    public long TotalParcels { get; set; }
    public Dictionary<int, long> ChuteDispatchCounts { get; set; } = new();
    public double SortingRunSeconds { get; set; }
    public TimeSpan? SortingDuration => TimeSpan.FromSeconds(Math.Max(0, SortingRunSeconds));
    public double? ParcelsPerHour => SortingRunSeconds > 0 ? TotalParcels * 3600d / SortingRunSeconds : 0;
    // Kept only for counter state written before rejection causes were tracked.
    public long Rejected { get; set; }
    public long RejectedShipmentNotFound { get; set; }
    public long RejectedRouteNotConfigured { get; set; }
    public long RejectedCode86RetryLimit { get; set; }
    public long RejectedMultipleShipments { get; set; }
    public long RejectedConfiguredRoute { get; set; }
    public long RejectedOther { get; set; }
    public long TotalRejected => Rejected + RejectedShipmentNotFound + RejectedRouteNotConfigured +
        RejectedCode86RetryLimit + RejectedMultipleShipments + RejectedConfiguredRoute + RejectedOther;

    public void CountRejection(string reason)
    {
        switch (reason)
        {
            // Counted independently of the destination by the camera classification.
            case "Pas dans le système": break;
            case "Route de l'expédition non configurée": RejectedRouteNotConfigured++; break;
            case "Limite de reprises code 86": RejectedCode86RetryLimit++; break;
            case "Plusieurs expéditions détectées": RejectedMultipleShipments++; break;
            case "Route de l'expédition":
            case "Route du code postal": RejectedConfiguredRoute++; break;
            default: RejectedOther++; break;
        }
    }

    public IEnumerable<(string Label, long Count)> RejectionCauses()
    {
        yield return ("Pas dans le système", RejectedShipmentNotFound);
        yield return ("Route non configurée", RejectedRouteNotConfigured);
        yield return ("Plusieurs code-barres", RejectedMultipleShipments);
    }
    public long NoReads { get; set; }
    public long Code98 { get; set; }
    public long Code98RecirculatedOverTwice { get; set; }
    public long Code68 { get; set; }
    public long Code97 { get; set; }
    public long DimensionErrors { get; set; }
    public long ScaleErrors { get; set; }
    public long ScaleFaults { get; set; }
    public long LightParcels { get; set; }
    public long SmallParcels { get; set; }
    public long InverseLengthParcels { get; set; }
    public long SortedWithoutIssue { get; set; }
    public long SortedByWaybill { get; set; }
    public long SortedByPostalCode { get; set; }
    public long DatabaseInserts { get; set; }
}

public sealed record ConnectionState(bool Camera, bool Dimensioner, bool Scale, bool Database, bool Plc, bool Simulated = false, bool DatabaseSimulated = false);

public sealed record DeviceReception(string Raw, DateTimeOffset ReceivedAt, long Sequence, bool Truncated = false);

public sealed record PlcDispatch(DateTimeOffset SentAt, int Chute, long ElapsedMs, long Sequence)
{
    public string? ParcelKey { get; init; }
}

public sealed record DatabaseReferenceCounts(long Parcels, long PostalCodes, long Scans, bool Connected, bool Simulated, DateTimeOffset UpdatedAt, bool HasOverdueScans = false, DateTimeOffset? LastShipmentUpdate = null, bool? HasRecentShipmentUpdates = null);

public sealed record LineSnapshot(
    int LineId,
    string Name,
    bool Running,
    ConnectionState Connections,
    LineCounters Counters,
    SortDecision? LastDecision,
    string? LastError,
    DateTimeOffset UpdatedAt,
    bool Code98Enabled = false,
    DeviceReception? CameraInput = null,
    DeviceReception? DimensionInput = null,
    DeviceReception? ScaleInput = null,
    DeviceReception? PlcInput = null,
    string PlcTag = "COLISDDE",
    bool PlcTagSupported = false,
    DeviceReception? PlcTransferInput = null,
    string PlcTransferTag = "",
    bool PlcTransferTagSupported = false,
    PlcDispatch? LastPlcDispatch = null,
    bool Maintenance = false,
    LineCounters? ProductionCounters = null,
    LineCounters? MaintenanceCounters = null,
    bool? ScaleFaultActive = null,
    DateTimeOffset? LastParcelReceivedAt = null,
    IReadOnlyList<PlcDispatch>? RecentPlcDispatches = null);
