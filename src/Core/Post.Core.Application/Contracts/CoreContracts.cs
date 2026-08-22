using Post.Core.Domain.Common;

namespace Post.Core.Application.Contracts;

public sealed record PagedResult<T>(IReadOnlyCollection<T> Items, int TotalCount);

public sealed record PhysicalAttributesDto(
    decimal WeightKg,
    decimal LengthCm,
    decimal WidthCm,
    decimal HeightCm);

public sealed record DiscrepancyDto(
    PhysicalAttributeKind Attribute,
    decimal ReferenceValue,
    decimal ObservedValue);

public sealed record InboundQueryCommand(
    string IdempotencyKey,
    IReadOnlyCollection<string> Barcodes,
    string ParcelType,
    string ServiceType,
    string OriginCenterCode,
    string DestinationCenterCode,
    Guid DeviceId,
    string ExchangeCenterCode,
    PhysicalAttributesDto PhysicalAttributes,
    DateTimeOffset EdgeEventTime,
    string CorrelationId);

public sealed record InboundQueryResult(
    string ParcelBarcode,
    CoreParcelStatus CoreStatus,
    DeviceParcelStatus DeviceStatus,
    string OriginCenterCode,
    string DestinationCenterCode,
    Guid ReadingRecordId,
    IReadOnlyCollection<DiscrepancyDto> Discrepancies,
    bool IsReplay);

public sealed record ParcelFinalEventCommand(
    Guid EventId,
    Guid EdgeId,
    string ExchangeCenterCode,
    string IdempotencyKey,
    string ParcelBarcode,
    CoreParcelStatus FinalStatus,
    string CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record ImageMetadataCommand(
    Guid EventId,
    Guid EdgeId,
    string ExchangeCenterCode,
    string ParcelBarcode,
    Guid ReadingRecordId,
    string ObjectKey,
    string ContentType,
    string AttachmentType,
    string CorrelationId,
    DateTimeOffset ReceivedAt);

public sealed record IngestionEventCommand(
    Guid EventId,
    string Topic,
    string IdempotencyKey,
    string CorrelationId,
    object Payload,
    DateTimeOffset OccurredAt,
    string? BusinessFingerprint = null);

public sealed record DurablePublishReceipt(Guid EventId, DateTimeOffset AcknowledgedAt);

public sealed record BagStoreCommand(
    string IdempotencyKey,
    string BagBarcode,
    string OriginCenterCode,
    string DestinationCenterCode,
    Guid CreatedByDeviceId,
    IReadOnlyCollection<string> MemberBarcodes,
    string SealNumber,
    string TransportType,
    DateTimeOffset ClosedAt,
    string CorrelationId);

public sealed record StorageResult(string Identifier, bool IsReplay);

public sealed record BagDto(
    string BagBarcode,
    string OriginCenterCode,
    string DestinationCenterCode,
    Guid CreatedByDeviceId,
    IReadOnlyCollection<string> MemberBarcodes,
    string SealNumber,
    string TransportType,
    DateTimeOffset ClosedAt,
    BagState State,
    string? AssignedDispatchId);

public sealed record DispatchStoreCommand(
    string IdempotencyKey,
    string DispatchId,
    string DestinationCenterCode,
    IReadOnlyCollection<string> BagBarcodes,
    DateTimeOffset ClosedAt,
    string CorrelationId);

public sealed record DispatchDto(
    string DispatchId,
    string DestinationCenterCode,
    IReadOnlyCollection<string> BagBarcodes,
    DateTimeOffset ClosedAt);

public sealed record RegisterDeviceCommand(
    string Name,
    string DeviceType,
    string LogicalCode,
    string Owner,
    string ExchangeCenterCode,
    string? Description,
    string? IdempotencyKey = null,
    string Actor = "system",
    string CorrelationId = "device-registration");

public sealed record RegisteredDeviceResult(
    Guid DeviceId,
    string? DeviceSecret,
    bool IsReplay = false,
    bool RecoveryRequired = false);

public sealed record RotateDeviceSecretCommand(
    Guid DeviceId,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    TimeSpan? GracePeriod = null);

public sealed record DeviceDto(
    Guid DeviceId,
    string Name,
    string DeviceType,
    string LogicalCode,
    string Owner,
    string ExchangeCenterCode,
    string Description,
    DeviceActivationStatus ActivationStatus);

public sealed record UpdateDeviceCommand(
    Guid DeviceId,
    string Name,
    string DeviceType,
    string Owner,
    string? Description,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record PublishConfigurationCommand(
    string ExchangeCenterCode,
    IReadOnlyCollection<string> PostalCodes,
    TimeSpan ShortReadingThreshold,
    TimeSpan ReturnThreshold,
    decimal WeightToleranceKg,
    decimal DimensionToleranceCm,
    string IdempotencyKey,
    string Actor,
    string CorrelationId,
    bool AutoDispatchEnabled = false,
    TimeSpan? MaximumOpenBagDuration = null);

public sealed record ConfigurationDeviceDto(
    Guid DeviceId,
    string LogicalCode,
    string Name,
    string DeviceType,
    DeviceActivationStatus ActivationStatus,
    string SecretAlgorithm,
    int SecretIterations,
    string SecretSalt,
    string SecretHash,
    string? PreviousSecretAlgorithm = null,
    int? PreviousSecretIterations = null,
    string? PreviousSecretSalt = null,
    string? PreviousSecretHash = null,
    DateTimeOffset? PreviousSecretValidUntil = null);

public sealed record ConfigurationSnapshotDto(
    string ExchangeCenterCode,
    long Version,
    DateTimeOffset GeneratedAt,
    IReadOnlyCollection<ConfigurationDeviceDto> Devices,
    IReadOnlyCollection<string> PostalCodes,
    TimeSpan ShortReadingThreshold,
    TimeSpan ReturnThreshold,
    decimal WeightToleranceKg,
    decimal DimensionToleranceCm,
    bool AutoDispatchEnabled = false,
    int MaximumOpenBagMinutes = 60);

public sealed record PresignedUrlCommand(
    Guid EventId,
    Guid EdgeId,
    string ExchangeCenterCode,
    Guid ReadingRecordId,
    string AttachmentType,
    string ContentType,
    long ContentLength,
    TimeSpan Lifetime);

public sealed record PresignedUrlResult(
    string ObjectKey,
    Uri UploadUrl,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> RequiredHeaders);

public sealed record RegisterEdgeCommand(
    Guid EdgeId,
    string ExchangeCenterCode,
    string SoftwareVersion,
    long ConfigurationVersion,
    DateTimeOffset RegisteredAt);

public sealed record EdgeHeartbeatCommand(
    Guid EdgeId,
    DateTimeOffset HeartbeatAt,
    string SoftwareVersion,
    long ConfigurationVersion,
    long AvailableDiskBytes,
    int PendingCount,
    int FailedCount,
    int DeadLetterCount,
    DateTimeOffset? LastSuccessfulSync);

public sealed record RecordOperationalEventCommand(
    OperationalEventType EventType,
    Guid EdgeId,
    Guid? DeviceId,
    string IdempotencyKey,
    string CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record EdgeHealthDto(
    Guid EdgeId,
    string ExchangeCenterCode,
    DateTimeOffset LastHeartbeat,
    string SoftwareVersion,
    long ConfigurationVersion,
    long AvailableDiskBytes,
    EdgeConnectionState ConnectionState,
    int PendingCount,
    int FailedCount,
    int DeadLetterCount,
    DateTimeOffset? LastSuccessfulSync);

public sealed record OperationalResultCommand(
    Guid EdgeId,
    string ExchangeCenterCode,
    string IdempotencyKey,
    string CorrelationId,
    string? ParcelBarcode,
    string CallResult,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CalledAt,
    DateTimeOffset RespondedAt,
    int Attempts,
    OperationalFinalStatus FinalStatus);

public static class AuthenticationInputLimits
{
    public const long TokenRequestBodyMaximumBytes = 8 * 1024;
    public const int UserNameMaximumLength = 128;
    public const int LogicalDeviceCodeMaximumLength = 64;
    public const int PasswordMaximumLength = 1024;
    public const int DeviceSecretMaximumLength = 1024;
}

public sealed record AuthenticateEdgeCommand(
    string LogicalDeviceCode,
    string DeviceSecret,
    TimeSpan TokenLifetime);

public sealed record ProvisionEdgeCredentialCommand(
    Guid DeviceId,
    string LogicalDeviceCode,
    string ExchangeCenterCode,
    string DeviceSecret,
    string Actor = "system",
    string CorrelationId = "edge-provisioning",
    string? IdempotencyKey = null);

public sealed record ProvisionedEdgeCredentialResult(
    Guid DeviceId,
    string? DeviceSecret,
    bool IsReplay = false,
    bool RecoveryRequired = false);

public sealed record RotateEdgeCredentialCommand(
    Guid DeviceId,
    string DeviceSecret,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record AuthenticateAdminCommand(
    string UserName,
    string Password,
    TimeSpan TokenLifetime);

public sealed record ProvisionInitialAdminCommand(
    string UserName,
    string Password,
    IReadOnlyCollection<AdminRole> Roles,
    IReadOnlyCollection<string> AllowedCenterCodes,
    string CorrelationId);

public sealed record ProvisionedAdminResult(Guid UserId, string UserName);

public sealed record AdminIdentityProfile(
    Guid UserId,
    string UserName,
    bool IsActive,
    IReadOnlyCollection<AdminRole> Roles,
    IReadOnlyCollection<string> AllowedCenterCodes);

public sealed record InitialAdminIdentity(
    Guid UserId,
    string UserName,
    string Password,
    IReadOnlyCollection<AdminRole> Roles,
    IReadOnlyCollection<string> AllowedCenterCodes);

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAt);

public sealed record AuditCommand(
    string Actor,
    string Action,
    string ResourceType,
    string ResourceId,
    string? Details,
    string CorrelationId);

public sealed record ParcelTrackingDto(
    string ParcelBarcode,
    string ParcelType,
    string ServiceType,
    string OriginCenterCode,
    string DestinationCenterCode,
    CoreParcelStatus? FinalStatus,
    int ReadingCount);

public sealed record AccessScope(
    bool IsGlobal,
    IReadOnlyCollection<string> AllowedCenterCodes)
{
    public static AccessScope Global { get; } = new(true, Array.Empty<string>());
}

public sealed record EventEnvelope(
    Guid EventId,
    string Topic,
    string Key,
    string Payload,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    int SchemaVersion = 1,
    string? BusinessFingerprint = null);

public sealed record TokenDescriptor(
    string Subject,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> AllowedCenters,
    IReadOnlyDictionary<string, string> AdditionalClaims,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public enum IdempotencyDecision
{
    New = 0,
    Replay = 1,
    InProgress = 2
}
