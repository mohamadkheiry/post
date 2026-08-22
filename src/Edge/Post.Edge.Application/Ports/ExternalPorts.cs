using Post.Edge.Application.Contracts;
using Post.Edge.Domain.Devices;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Application.Ports;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IDiskSpaceProbe
{
    long GetAvailableBytes();
}

public interface IDeviceCredentialVerifier
{
    bool Verify(DeviceSecretVerifier? configuredVerifier, string suppliedSecret);
}

public interface IDestinationLookupClient
{
    Task<string?> ResolveAsync(string fourteenDigitBarcode, CancellationToken cancellationToken);
}

public sealed record ParcelInfoResult(
    decimal? Weight,
    string? BoxSize,
    decimal? PostalCharge,
    bool? IsJoof);

public interface IParcelInfoClient
{
    Task<ParcelInfoResult?> GetAsync(
        string centerCode,
        string parcelBarcode,
        int userId,
        CancellationToken cancellationToken);
}

public sealed record DeviceEvent(
    string MessageType,
    string PayloadJson,
    string IdempotencyKey);

public interface IDeviceEventPublisher
{
    Task PublishAsync(DeviceEvent message, CancellationToken cancellationToken);
}

public sealed record CoreImageUpload(
    Guid ReadId,
    Guid ReadingRecordId,
    string ParcelBarcode,
    string ImageId,
    byte[] Content,
    string? MimeType,
    string? Description,
    string IdempotencyKey,
    DateTimeOffset CapturedAt,
    string AttachmentType = "parcel-image");

public interface ICoreImageUploadClient
{
    Task UploadAsync(CoreImageUpload image, CancellationToken cancellationToken);
}

public enum CoreParcelDisposition
{
    Normal,
    Returned,
    Refused
}

public sealed record CoreInboundLookupResult(
    CoreParcelDisposition Disposition,
    string? DiscrepancyCode,
    string? DestinationCode,
    Guid? ReadingRecordId = null);

public sealed record CoreInboundLookupRequest(
    Guid OperationId,
    string DeviceId,
    IReadOnlyCollection<string> Barcodes,
    string ParcelType,
    string ServiceType,
    string OriginCenterCode,
    string DestinationCenterCode,
    int? WeightGrams,
    int? LengthMillimeters,
    int? WidthMillimeters,
    int? HeightMillimeters,
    DateTimeOffset EdgeEventTime,
    string CorrelationId);

public sealed record DeferredCoreInboundLookup(
    CoreInboundLookupRequest Request,
    Guid ReadId,
    string RequestHash,
    string ParcelBarcode,
    string OriginCenterCode,
    string DestinationCenterCode,
    IReadOnlyCollection<string> ImageIds,
    bool HasSupplementaryData,
    DateTimeOffset CapturedAt);

public interface ICoreApiClient
{
    Task<CoreInboundLookupResult> LookupInboundAsync(
        CoreInboundLookupRequest request,
        CancellationToken cancellationToken);
    Task PublishAsync(
        string messageType,
        string payloadJson,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public sealed record CoreEdgeRegistration(
    Guid EdgeId,
    string ExchangeCenterCode,
    string SoftwareVersion,
    long ConfigurationVersion,
    DateTimeOffset RegisteredAt);

public sealed record CoreEdgeHeartbeat(
    Guid EdgeId,
    DateTimeOffset HeartbeatAt,
    string SoftwareVersion,
    long ConfigurationVersion,
    long AvailableDiskBytes,
    int PendingCount,
    int FailedCount,
    int DeadLetterCount,
    DateTimeOffset? LastSuccessfulSync);

public interface ICoreEdgeHealthClient
{
    Task RegisterAsync(CoreEdgeRegistration registration, CancellationToken cancellationToken);
    Task SendHeartbeatAsync(CoreEdgeHeartbeat heartbeat, CancellationToken cancellationToken);
}

public sealed record CoreOperationalResultMessage(
    string CorrelationId,
    string? ParcelBarcode,
    string CallResult,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CalledAt,
    DateTimeOffset RespondedAt,
    int Attempts,
    string FinalStatus);

public sealed record CoreDeviceOperationalEvent(
    string? DeviceId,
    string CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record PostalInboundCommand(
    Guid OperationId,
    Guid ReadId,
    string DeviceId,
    BarcodeSet Barcodes,
    string DestinationCode,
    PhysicalAttributes Physical,
    string? ParcelType,
    string? ServiceType,
    DateTimeOffset ReadTimestamp,
    TimeSpan Timeout);

public enum PostalInboundStatus
{
    Failed,
    Registered,
    Pending
}

public sealed record PostalInboundResult(
    PostalInboundStatus Status,
    string? TrackingId,
    string? ErrorCode,
    string? ErrorMessage);

public interface IPostalInboundClient
{
    Task<PostalInboundResult> RegisterAsync(PostalInboundCommand command, CancellationToken cancellationToken);
    Task<PostalInboundResult> GetPendingResultAsync(string trackingId, CancellationToken cancellationToken);
}

public sealed record PostalBagParcel(
    Guid ReadId,
    string CanonicalBarcode,
    bool IsDeferred,
    decimal? Weight,
    string? ServiceType);

public sealed record PostalBagCommand(
    Guid OperationId,
    string DeviceId,
    string DestinationCode,
    string SealNumber,
    string TransportType,
    IReadOnlyCollection<PostalBagParcel> Parcels);

public enum PostalBagStatus
{
    Failed,
    Completed,
    Unavailable
}

public sealed record PostalParcelFailure(
    Guid ReadId,
    string ErrorCode,
    string Description,
    string? Category);

public sealed record PostalBagResult(
    PostalBagStatus Status,
    string? BagBarcode,
    IReadOnlyCollection<PostalParcelFailure> Failures,
    string? ErrorCode,
    string? ErrorMessage);

public interface IPostalBagClient
{
    Task<PostalBagResult> CloseAsync(PostalBagCommand command, CancellationToken cancellationToken);
}

public sealed record PostalDispatchCommand(
    Guid OperationId,
    string DeviceId,
    string DestinationCode,
    IReadOnlyCollection<string> BagBarcodes);

public sealed record PostalDispatchResult(
    bool IsSuccess,
    string? DispatchId,
    string? ErrorCode,
    string? ErrorMessage);

public interface IPostalDispatchClient
{
    Task<PostalDispatchResult> CloseAsync(PostalDispatchCommand command, CancellationToken cancellationToken);
}

public interface IBagLabelGenerator
{
    string Generate(
        string bagBarcode,
        string deviceId,
        string destinationCode,
        BagCounts counts,
        DateTimeOffset createdAt);
}

public interface IConfigurationSource
{
    Task<EdgeConfigurationDto?> FetchAsync(long? currentVersion, CancellationToken cancellationToken);
}
