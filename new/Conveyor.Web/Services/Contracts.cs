using Conveyor.Web.Domain;

namespace Conveyor.Web.Services;

public interface IConveyorRepository
{
    bool IsSimulation { get; }
    Task SaveConveyorActionAsync(int conveyorId, bool start, int? cause, CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetLastShipmentUpdateAsync(CancellationToken cancellationToken);
    Task ResetDataAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ConveyorShift>> GetShiftsAsync(CancellationToken cancellationToken);
    Task<Shipment?> FindShipmentAsync(string barcode, CancellationToken cancellationToken);
    Task<int?> FindChuteForRouteAsync(int shiftId, int routeId, CancellationToken cancellationToken);
    Task<int?> FindChuteForPostalCodeAsync(int shiftId, string postalCode, CancellationToken cancellationToken);
    Task<bool> ShouldUseExceptionChuteAsync(string codeType, string barcode, int retryLimit, CancellationToken cancellationToken);
    Task<int> RecordExceptionPassAsync(string codeType, string barcode, CancellationToken cancellationToken);
    Task ClearExceptionCodeAsync(string codeType, string barcode, CancellationToken cancellationToken);
    Task SaveScanAsync(int lineId, int? databaseLineId, ParcelContext parcel, SortDecision decision, CancellationToken cancellationToken);
    Task<bool> PingAsync(CancellationToken cancellationToken);
    Task<(long Parcels, long PostalCodes, long Scans, bool HasOverdueScans)> GetReferenceCountsAsync(CancellationToken cancellationToken, long? cachedPostalCodes = null);
}

public interface IDatabaseMetricsService
{
    event Action? Changed;
    DatabaseReferenceCounts Current { get; }
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task RefreshAfterResetAsync(CancellationToken cancellationToken = default);
}

public interface IPlcGateway
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync();
    Task SendChuteAsync(string tag, int chute, int repeat, CancellationToken cancellationToken);
    Task<bool> PingAsync(CancellationToken cancellationToken);
}

public interface IPlcReadback
{
    bool ReadsHealthy { get; }
    event Action<string, string>? TagChanged;
}

public interface IConveyorSupervisor
{
    event Action? Changed;
    IReadOnlyList<LineSnapshot> GetSnapshots();
    int CurrentShiftId { get; }
    bool? ConveyorRunning { get; }
    bool Maintenance { get; }
    bool HasStartedOperatingMode { get; }
    bool CanChangeOperatingMode { get; }
    Task SetShiftAsync(int shiftId);
    Task<ConveyorActionResult> SetConveyorMotionAsync(bool start, int? cause, bool maintenance = false);
    Task StartLineAsync(int lineId);
    Task RestartLineAsync(int lineId);
    Task<string> RestartRslinxAsync();
    bool RslinxRestartInProgress { get; }
    Task<string> RestartRslinxAutomaticallyAsync(CancellationToken token = default);
    Task StopLineAsync(int lineId);
    void ResetCounters(int lineId);
    void SetCode98Enabled(int lineId, bool enabled);
    Task TriggerScaleFaultTestAsync(int lineId);
    Task SimulateParcelAsync(int lineId, string cameraData, Dimension dimension, decimal weight);
}
