using System.Text.Json;
using Post.Contracts.V1.Common;

namespace Post.Contracts.V1.EdgeCore;

/// <summary>One heterogeneous envelope in an edge synchronization batch.</summary>
/// <param name="MessageId">Idempotency key.</param>
/// <param name="MessageType">Stable payload type.</param>
/// <param name="SchemaVersion">Payload schema version.</param>
/// <param name="CorrelationId">End-to-end correlation identifier.</param>
/// <param name="CausationId">Causing message, if any.</param>
/// <param name="OccurredAtUtc">UTC business occurrence time.</param>
/// <param name="Sequence">Strict edge sequence.</param>
/// <param name="Payload">Serialized typed payload.</param>
public sealed record EdgeSyncItemV1(
    Guid MessageId,
    string MessageType,
    string SchemaVersion,
    string CorrelationId,
    Guid? CausationId,
    DateTimeOffset OccurredAtUtc,
    long Sequence,
    JsonElement Payload);

/// <summary>Atomic transport batch from one edge node. Items are acknowledged independently.</summary>
/// <param name="BatchId">Batch correlation identifier.</param>
/// <param name="EdgeId">Registered edge identifier.</param>
/// <param name="CenterCode">Exchange-center code.</param>
/// <param name="ConfigurationVersion">Configuration active when the batch was created.</param>
/// <param name="Items">Sequence-ordered messages.</param>
public sealed record EdgeSyncBatchRequestV1(
    Guid BatchId,
    string EdgeId,
    string CenterCode,
    long ConfigurationVersion,
    IReadOnlyList<EdgeSyncItemV1> Items);

/// <summary>Acknowledgement for one synchronization item.</summary>
/// <param name="MessageId">Original message identifier.</param>
/// <param name="Sequence">Original sequence.</param>
/// <param name="Status">Durable processing outcome.</param>
/// <param name="RetryAfterSeconds">Suggested retry delay for transient outcomes.</param>
/// <param name="Error">Safe rejection/failure details.</param>
public sealed record EdgeSyncAcknowledgementV1(
    Guid MessageId,
    long Sequence,
    CoreSyncItemStatusV1 Status,
    int? RetryAfterSeconds = null,
    ContractErrorV1? Error = null);

/// <summary>Core response to an edge synchronization batch.</summary>
/// <param name="BatchId">Original batch identifier.</param>
/// <param name="ReceivedAtUtc">UTC receipt time.</param>
/// <param name="Acknowledgements">Per-item outcomes.</param>
public sealed record EdgeSyncBatchResponseV1(
    Guid BatchId,
    DateTimeOffset ReceivedAtUtc,
    IReadOnlyList<EdgeSyncAcknowledgementV1> Acknowledgements);

/// <summary>Normalized physical measurements carried from edge to core.</summary>
/// <param name="WeightGrams">Measured weight.</param>
/// <param name="LengthMillimeters">Measured length.</param>
/// <param name="WidthMillimeters">Measured width.</param>
/// <param name="HeightMillimeters">Measured height.</param>
public sealed record CorePhysicalAttributesV1(
    int? WeightGrams = null,
    int? LengthMillimeters = null,
    int? WidthMillimeters = null,
    int? HeightMillimeters = null);

/// <summary>A durable parcel reading produced by edge.</summary>
/// <param name="OperationId">Device operation idempotency key.</param>
/// <param name="CanonicalBarcode">Canonical parcel barcode.</param>
/// <param name="BarcodeAliases">Other observed barcodes.</param>
/// <param name="DeviceId">Producing device.</param>
/// <param name="ReadAtUtc">UTC reading time.</param>
/// <param name="Physical">Measured attributes.</param>
/// <param name="RegistrationStatus">Postal entry-registration result.</param>
/// <param name="Classification">Reading classification when known.</param>
/// <param name="OriginCode">Resolved origin.</param>
/// <param name="DestinationCode">Resolved destination.</param>
public sealed record ParcelReadingSubmittedV1(
    Guid OperationId,
    string CanonicalBarcode,
    IReadOnlyList<string> BarcodeAliases,
    string DeviceId,
    DateTimeOffset ReadAtUtc,
    CorePhysicalAttributesV1? Physical,
    CoreOperationStatusV1 RegistrationStatus,
    CoreReadingClassificationV1? Classification,
    string? OriginCode,
    string? DestinationCode);

/// <summary>Reports a parcel lifecycle transition made at edge.</summary>
/// <param name="OperationId">Idempotency key for the transition.</param>
/// <param name="ParcelBarcode">Canonical parcel barcode.</param>
/// <param name="PreviousStatus">Previous status, absent for initial state.</param>
/// <param name="CurrentStatus">New status.</param>
/// <param name="ChangedAtUtc">UTC transition time.</param>
/// <param name="DestinationCode">Current operational destination.</param>
/// <param name="ChuteId">Current chute.</param>
/// <param name="Error">Associated safe business/upstream error.</param>
public sealed record ParcelStatusChangedV1(
    Guid OperationId,
    string ParcelBarcode,
    CoreParcelLifecycleStatusV1? PreviousStatus,
    CoreParcelLifecycleStatusV1 CurrentStatus,
    DateTimeOffset ChangedAtUtc,
    string? DestinationCode = null,
    string? ChuteId = null,
    ContractErrorV1? Error = null);

/// <summary>Normalized postal bag counters.</summary>
/// <param name="N">New parcels.</param>
/// <param name="P">Deferred parcels.</param>
/// <param name="M">New parcels with errors.</param>
/// <param name="Q">Deferred parcels with errors.</param>
public sealed record CoreBagClosureCountsV1(int N, int P, int M, int Q);

/// <summary>Final closed-bag fact sent to core.</summary>
/// <param name="OperationId">Original bag-close operation.</param>
/// <param name="BagBarcode">Upstream-issued identity.</param>
/// <param name="DeviceId">Creating device.</param>
/// <param name="OriginCenterCode">Origin center.</param>
/// <param name="DestinationCode">Destination center.</param>
/// <param name="ParcelBarcodes">Immutable informational members.</param>
/// <param name="SealNumber">Physical seal number.</param>
/// <param name="TransportType">Transport mode.</param>
/// <param name="ClosedAtUtc">UTC closure time.</param>
/// <param name="Counts">Postal closure counters.</param>
/// <param name="LabelSha256">Digest of the separately stored label.</param>
public sealed record BagClosedV1(
    Guid OperationId,
    string BagBarcode,
    string DeviceId,
    string OriginCenterCode,
    string DestinationCode,
    IReadOnlyList<string> ParcelBarcodes,
    string SealNumber,
    string TransportType,
    DateTimeOffset ClosedAtUtc,
    CoreBagClosureCountsV1 Counts,
    string? LabelSha256 = null);

/// <summary>Per-device statistics persisted for a dispatch.</summary>
/// <param name="DeviceId">Device identifier.</param>
/// <param name="BagCount">Bag count.</param>
/// <param name="ParcelCount">Parcel count.</param>
/// <param name="ParcelWithErrorCount">Problem parcel count.</param>
public sealed record CoreDispatchDeviceStatisticsV1(
    string DeviceId,
    int BagCount,
    int ParcelCount,
    int ParcelWithErrorCount);

/// <summary>Final closed-dispatch fact sent to core.</summary>
/// <param name="OperationId">Original dispatch-close operation.</param>
/// <param name="DispatchId">Upstream-issued identity.</param>
/// <param name="DestinationCode">Destination center.</param>
/// <param name="BagBarcodes">Immutable bag members.</param>
/// <param name="ClosedAtUtc">UTC closure time.</param>
/// <param name="Automatic">Whether closure was policy-driven.</param>
/// <param name="Statistics">Per-device statistics.</param>
public sealed record DispatchClosedV1(
    Guid OperationId,
    string DispatchId,
    string DestinationCode,
    IReadOnlyList<string> BagBarcodes,
    DateTimeOffset ClosedAtUtc,
    bool Automatic,
    IReadOnlyList<CoreDispatchDeviceStatisticsV1> Statistics);

/// <summary>Metadata linking asynchronously uploaded content to a parcel reading.</summary>
/// <param name="AttachmentId">Attachment idempotency key.</param>
/// <param name="ParcelBarcode">Canonical parcel barcode.</param>
/// <param name="ReadingOperationId">Reading that produced the attachment.</param>
/// <param name="ObjectKey">Opaque object-store key.</param>
/// <param name="MediaType">IANA media type.</param>
/// <param name="LengthBytes">Content size.</param>
/// <param name="Sha256">Content digest.</param>
/// <param name="CapturedAtUtc">UTC capture time.</param>
public sealed record AttachmentMetadataSubmittedV1(
    Guid AttachmentId,
    string ParcelBarcode,
    Guid ReadingOperationId,
    string ObjectKey,
    string MediaType,
    long LengthBytes,
    string Sha256,
    DateTimeOffset CapturedAtUtc);

/// <summary>Append-only audit event produced by edge.</summary>
/// <param name="EventName">Stable event name.</param>
/// <param name="Category">Audit category.</param>
/// <param name="OccurredAtUtc">UTC event time.</param>
/// <param name="DeviceId">Related device.</param>
/// <param name="ActorId">Related authenticated actor.</param>
/// <param name="SubjectId">Related business subject.</param>
/// <param name="Properties">Safe, non-secret scalar properties.</param>
public sealed record OperationalEventReportedV1(
    string EventName,
    string Category,
    DateTimeOffset OccurredAtUtc,
    string? DeviceId = null,
    string? ActorId = null,
    string? SubjectId = null,
    IReadOnlyDictionary<string, string>? Properties = null);

/// <summary>Health and backlog heartbeat sent periodically by an edge node.</summary>
/// <param name="ObservedAtUtc">UTC observation time.</param>
/// <param name="ApplicationVersion">Running edge application version.</param>
/// <param name="ConfigurationVersion">Applied configuration version.</param>
/// <param name="ConnectedDeviceCount">Authenticated WebSocket sessions.</param>
/// <param name="PendingOutboxCount">Pending synchronization messages.</param>
/// <param name="OldestOutboxAgeSeconds">Age of the oldest pending message.</param>
/// <param name="AvailableDiskBytes">Available durable-storage capacity.</param>
public sealed record EdgeHeartbeatV1(
    DateTimeOffset ObservedAtUtc,
    string ApplicationVersion,
    long ConfigurationVersion,
    int ConnectedDeviceCount,
    long PendingOutboxCount,
    long? OldestOutboxAgeSeconds,
    long AvailableDiskBytes);

/// <summary>Requests the latest configuration newer than an edge version.</summary>
/// <param name="EdgeId">Registered edge identifier.</param>
/// <param name="CenterCode">Exchange center.</param>
/// <param name="CurrentVersion">Currently applied version.</param>
public sealed record ConfigurationPullRequestV1(string EdgeId, string CenterCode, long CurrentVersion);

/// <summary>Offline authentication entry for a device. It never contains a raw token.</summary>
/// <param name="DeviceId">Device identity.</param>
/// <param name="LogicalCode">Globally unique logical code.</param>
/// <param name="DeviceTokenHash">Versioned password-hash representation.</param>
/// <param name="Active">Whether new/authenticated sessions are allowed.</param>
public sealed record DeviceRegistryEntryV1(
    string DeviceId,
    string LogicalCode,
    string DeviceTokenHash,
    bool Active);

/// <summary>Operational policy values for one exchange center.</summary>
/// <param name="AutoDispatchEnabled">Whether automatic dispatch closure is enabled.</param>
/// <param name="MaximumOpenBagMinutes">Maximum open duration before automatic dispatch.</param>
/// <param name="ShortTermRereadHours">Short-term reread threshold.</param>
/// <param name="ReturnClassificationHours">Return classification threshold.</param>
public sealed record CenterOperationsPolicyV1(
    bool AutoDispatchEnabled,
    int MaximumOpenBagMinutes,
    int ShortTermRereadHours,
    int ReturnClassificationHours);

/// <summary>Postal reference point distributed to edge.</summary>
/// <param name="Code">Postal point code.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="Active">Whether routing may select the point.</param>
public sealed record PostalPointReferenceV1(string Code, string DisplayName, bool Active);

/// <summary>Immutable, versioned configuration published to one edge center.</summary>
/// <param name="CenterCode">Exchange center.</param>
/// <param name="Version">Strictly increasing version.</param>
/// <param name="PublishedAtUtc">UTC publication time.</param>
/// <param name="Devices">Offline device registry.</param>
/// <param name="Policy">Operational policy.</param>
/// <param name="PostalPoints">Reference data required for offline routing.</param>
public sealed record ConfigurationSnapshotV1(
    string CenterCode,
    long Version,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<DeviceRegistryEntryV1> Devices,
    CenterOperationsPolicyV1 Policy,
    IReadOnlyList<PostalPointReferenceV1> PostalPoints);

/// <summary>Response to a configuration pull.</summary>
/// <param name="Changed">Whether a newer snapshot exists.</param>
/// <param name="Snapshot">New immutable snapshot.</param>
/// <param name="ETag">Opaque cache validator.</param>
public sealed record ConfigurationPullResponseV1(
    bool Changed,
    ConfigurationSnapshotV1? Snapshot,
    string? ETag);

/// <summary>Requests a short-lived direct-upload URL for an attachment.</summary>
/// <param name="AttachmentId">Attachment idempotency key.</param>
/// <param name="ParcelBarcode">Related parcel.</param>
/// <param name="MediaType">IANA media type.</param>
/// <param name="LengthBytes">Expected content size.</param>
/// <param name="Sha256">Expected digest.</param>
public sealed record CreateAttachmentUploadRequestV1(
    Guid AttachmentId,
    string ParcelBarcode,
    string MediaType,
    long LengthBytes,
    string Sha256);

/// <summary>Short-lived direct-upload instructions.</summary>
/// <param name="AttachmentId">Attachment identifier.</param>
/// <param name="ObjectKey">Opaque key to use in metadata.</param>
/// <param name="UploadUri">Pre-signed URI. It must never be logged.</param>
/// <param name="ExpiresAtUtc">UTC expiry.</param>
/// <param name="RequiredHeaders">Headers that must accompany upload.</param>
public sealed record CreateAttachmentUploadResponseV1(
    Guid AttachmentId,
    string ObjectKey,
    Uri UploadUri,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyDictionary<string, string> RequiredHeaders);
