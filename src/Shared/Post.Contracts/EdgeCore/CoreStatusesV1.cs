using System.Text.Json.Serialization;
using Post.Contracts.V1.Device;

namespace Post.Contracts.V1.EdgeCore;

/// <summary>Core-facing normalized operation status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoreOperationStatusV1>))]
public enum CoreOperationStatusV1
{
    /// <summary>Operation is durably accepted for asynchronous work.</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,
    /// <summary>Operation is waiting for an external result.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,
    /// <summary>Operation completed successfully.</summary>
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,
    /// <summary>A business rule or upstream rejected the operation.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,
    /// <summary>A technical failure ended the operation.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed
}

/// <summary>Core business classification of a reading.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoreReadingClassificationV1>))]
public enum CoreReadingClassificationV1
{
    /// <summary>A normal first reading.</summary>
    [JsonStringEnumMemberName("normal")]
    Normal,
    /// <summary>A reread inside the configured short-term window.</summary>
    [JsonStringEnumMemberName("short-term-reread")]
    ShortTermReread,
    /// <summary>A parcel returned after export.</summary>
    [JsonStringEnumMemberName("returned")]
    Returned,
    /// <summary>A parcel refused and returning to origin.</summary>
    [JsonStringEnumMemberName("return-to-origin")]
    ReturnToOrigin
}

/// <summary>Core-facing parcel lifecycle status.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoreParcelLifecycleStatusV1>))]
public enum CoreParcelLifecycleStatusV1
{
    /// <summary>Entry registration succeeded.</summary>
    [JsonStringEnumMemberName("registered")]
    Registered,
    /// <summary>Operational destination was assigned.</summary>
    [JsonStringEnumMemberName("destination-assigned")]
    DestinationAssigned,
    /// <summary>Export is durably queued while postal upstream is unavailable.</summary>
    [JsonStringEnumMemberName("awaiting-upstream")]
    AwaitingUpstream,
    /// <summary>Export registration succeeded.</summary>
    [JsonStringEnumMemberName("export-registered")]
    ExportRegistered,
    /// <summary>Parcel belongs to an informationally closed bag.</summary>
    [JsonStringEnumMemberName("bagged")]
    Bagged,
    /// <summary>Upstream rejected export and a later retry is allowed.</summary>
    [JsonStringEnumMemberName("deferred")]
    Deferred,
    /// <summary>Parcel remained unresolved at operational-day close.</summary>
    [JsonStringEnumMemberName("unresolved")]
    Unresolved
}

/// <summary>Core processing outcome for an edge synchronization item.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CoreSyncItemStatusV1>))]
public enum CoreSyncItemStatusV1
{
    /// <summary>Item was durably accepted.</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,
    /// <summary>The idempotency key had already completed.</summary>
    [JsonStringEnumMemberName("duplicate")]
    Duplicate,
    /// <summary>Item is invalid and must not be retried unchanged.</summary>
    [JsonStringEnumMemberName("rejected")]
    Rejected,
    /// <summary>A transient condition requires retry.</summary>
    [JsonStringEnumMemberName("retry")]
    Retry
}

/// <summary>Explicit translation from device protocol statuses to normalized core statuses.</summary>
public static class DeviceToCoreStatusMapperV1
{
    /// <summary>Maps a device comprehensive status to core operation status.</summary>
    public static CoreOperationStatusV1 ToCoreOperationStatus(this DeviceStatusCodeV1 status) => status switch
    {
        DeviceStatusCodeV1.Success or DeviceStatusCodeV1.Returned or DeviceStatusCodeV1.Refused => CoreOperationStatusV1.Succeeded,
        DeviceStatusCodeV1.Pending => CoreOperationStatusV1.Pending,
        DeviceStatusCodeV1.Error => CoreOperationStatusV1.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported device status.")
    };

    /// <summary>Extracts reading classification carried by a device status.</summary>
    public static CoreReadingClassificationV1? ToCoreReadingClassification(this DeviceStatusCodeV1 status) => status switch
    {
        DeviceStatusCodeV1.Success or DeviceStatusCodeV1.Pending or DeviceStatusCodeV1.Error => null,
        DeviceStatusCodeV1.Returned => CoreReadingClassificationV1.Returned,
        DeviceStatusCodeV1.Refused => CoreReadingClassificationV1.ReturnToOrigin,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported device status.")
    };

    /// <summary>Maps destination-assignment status to core operation status.</summary>
    public static CoreOperationStatusV1 ToCoreOperationStatus(this DestinationAssignmentStatusCodeV1 status) => status switch
    {
        DestinationAssignmentStatusCodeV1.Confirmed => CoreOperationStatusV1.Succeeded,
        DestinationAssignmentStatusCodeV1.Error => CoreOperationStatusV1.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported destination-assignment status.")
    };

    /// <summary>Maps detailed bag-close result to normalized core operation status.</summary>
    public static CoreOperationStatusV1 ToCoreOperationStatus(this BagCloseResultTypeV1 result) => result switch
    {
        BagCloseResultTypeV1.Completed => CoreOperationStatusV1.Succeeded,
        BagCloseResultTypeV1.LabelPending => CoreOperationStatusV1.Pending,
        BagCloseResultTypeV1.NoEligibleParcels or BagCloseResultTypeV1.AllParcelsFailed => CoreOperationStatusV1.Rejected,
        BagCloseResultTypeV1.Error => CoreOperationStatusV1.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unsupported bag-close result.")
    };

    /// <summary>Maps detailed dispatch-close result to normalized core operation status.</summary>
    public static CoreOperationStatusV1 ToCoreOperationStatus(this DispatchCloseResultTypeV1 result) => result switch
    {
        DispatchCloseResultTypeV1.Completed => CoreOperationStatusV1.Succeeded,
        DispatchCloseResultTypeV1.Error => CoreOperationStatusV1.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Unsupported dispatch-close result.")
    };
}
