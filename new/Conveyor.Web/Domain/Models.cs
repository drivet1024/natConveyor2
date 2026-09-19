namespace Conveyor.Web.Domain;

public sealed record Dimension(decimal Length, decimal Width, decimal Height, int Status = 0)
{
    public static readonly Dimension Missing = new(-1, -1, -1, -1);
    public bool IsValid(decimal maximum) => Length > 0 && Width > 0 && Height > 0 &&
                                             Length <= maximum && Width <= maximum && Height <= maximum;
}

public sealed record TimedValue<T>(T Value, DateTimeOffset Timestamp);
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
    bool? DisableCode98 = null);

public sealed class LineCounters
{
    public long CameraReads { get; set; }
    public long DimensionReads { get; set; }
    public long ScaleReads { get; set; }
    public long TotalParcels { get; set; }
    public long Rejected { get; set; }
    public long NoReads { get; set; }
    public long Code98 { get; set; }
    public long DimensionErrors { get; set; }
    public long ScaleErrors { get; set; }
    public long SortedByWaybill { get; set; }
    public long SortedByPostalCode { get; set; }
    public long DatabaseInserts { get; set; }
}

public sealed record ConnectionState(bool Camera, bool Dimensioner, bool Scale, bool Database, bool Plc, bool Simulated = false, bool DatabaseSimulated = false);

public sealed record DeviceReception(string Raw, DateTimeOffset ReceivedAt, long Sequence, bool Truncated = false);

public sealed record DatabaseReferenceCounts(long Parcels, long PostalCodes, long Scans, bool Connected, bool Simulated, DateTimeOffset UpdatedAt, bool HasOverdueScans = false);

public sealed record LineSnapshot(
    int LineId,
    string Name,
    bool Running,
    ConnectionState Connections,
    LineCounters Counters,
    SortDecision? LastDecision,
    string? LastError,
    DateTimeOffset UpdatedAt,
    bool Code98Enabled = true,
    DeviceReception? CameraInput = null,
    DeviceReception? DimensionInput = null,
    DeviceReception? ScaleInput = null);
