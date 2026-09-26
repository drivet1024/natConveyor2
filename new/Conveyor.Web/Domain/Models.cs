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
    DateTimeOffset? WeightTimestamp);

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
    int? Code98PassCount = null);

public sealed class LineCounters
{
    public LineCounters Copy() => (LineCounters)MemberwiseClone();
    public long CameraReads { get; set; }
    public long DimensionReads { get; set; }
    public long ScaleReads { get; set; }
    public long TotalParcels { get; set; }
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
            case "Pas dans le système": RejectedShipmentNotFound++; break;
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

public sealed record PlcDispatch(DateTimeOffset SentAt, int Chute, long ElapsedMs, long Sequence);

public sealed record DatabaseReferenceCounts(long Parcels, long PostalCodes, long Scans, bool Connected, bool Simulated, DateTimeOffset UpdatedAt, bool HasOverdueScans = false, DateTimeOffset? LastShipmentUpdate = null, bool? HasOverdueShipments = null);

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
    LineCounters? MaintenanceCounters = null);
