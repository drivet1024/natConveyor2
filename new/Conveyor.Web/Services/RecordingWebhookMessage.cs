namespace Conveyor.Web.Services;

public sealed record RecordingWebhookJob(string Id, ParcelRecordingEvent Parcel, string CameraName,
    DateTimeOffset ArrivalAt, bool Arrived, bool Simulated);

// Each message replaces the previous schedule. An empty schedule cancels pending recordings.
public sealed record RecordingWebhookMessage(int Version, Guid Session, long Sequence,
    DateTimeOffset SentAt, bool Enabled, IReadOnlyList<RecordingWebhookJob> Jobs);
