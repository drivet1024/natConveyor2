using Conveyor.Web.Domain;

namespace Conveyor.Web.Services;

public interface IConveyorRepository
{
    bool IsSimulation { get; }
    Task SaveConveyorActionAsync(int conveyorId, bool start, int? cause, CancellationToken cancellationToken);
    Task<DateTimeOffset?> GetLastShipmentUpdateAsync(CancellationToken cancellationToken);
    Task<bool?> HasRecentShipmentUpdatesAsync(CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
    Task ResetDataAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ConveyorShift>> GetShiftsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<int, string>> GetChuteDestinationsAsync(int shiftId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    Task<Shipment?> FindShipmentAsync(string barcode, CancellationToken cancellationToken);
    async Task<IReadOnlyDictionary<string, Shipment>> FindShipmentsAsync(
        IReadOnlyCollection<string> barcodes, CancellationToken cancellationToken)
    {
        var shipments = new Dictionary<string, Shipment>(StringComparer.OrdinalIgnoreCase);
        foreach (var barcode in barcodes)
        {
            var shipment = await FindShipmentAsync(barcode, cancellationToken);
            if (shipment is not null) shipments.TryAdd(barcode, shipment);
        }
        return shipments;
    }
    Task<IReadOnlyList<string>> GetShipmentPrefixesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>([]);
    async Task<IReadOnlyList<ShipmentCustomerPrefix>> GetShipmentCustomerPrefixesAsync(CancellationToken cancellationToken) =>
        (await GetShipmentPrefixesAsync(cancellationToken)).Select(prefix => new ShipmentCustomerPrefix(null, prefix)).ToArray();
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
    int? ConveyorId => null;
    string? ConveyorStopCause => null;
    int? FullChutesCount { get; }
    long? Chute4FullTransitions => null;
    int? Code42Count { get; }
    bool Chute4AlarmActive { get; }
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
    Task SetLineMotionAsync(int lineId, bool start);
    Task TriggerScaleFaultTestAsync(int lineId);
    Task SimulateParcelAsync(int lineId, string cameraData, Dimension dimension, decimal weight);
}
