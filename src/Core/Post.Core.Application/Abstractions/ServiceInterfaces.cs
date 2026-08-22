using Post.Core.Application.Contracts;
using Post.Core.Domain.Messaging;

namespace Post.Core.Application.Abstractions;

public interface IInboundValidationService
{
    Task<DurablePublishReceipt> ValidateAndPublishAsync(
        IngestionEventCommand command,
        CancellationToken cancellationToken = default);
}

public interface IInboundQueryService
{
    Task<InboundQueryResult> ExecuteAsync(
        InboundQueryCommand command,
        CancellationToken cancellationToken = default);
}

public interface IParcelEventConsumer
{
    Task<bool> ConsumeAsync(
        ParcelFinalEventCommand command,
        CancellationToken cancellationToken = default);
}

public interface IAttachmentLinkingService
{
    Task<bool> LinkAsync(ImageMetadataCommand command, CancellationToken cancellationToken = default);
}

public interface IParcelTrackingQueryService
{
    Task<ParcelTrackingDto?> FindAsync(
        string barcode,
        AccessScope accessScope,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ParcelTrackingDto>> SearchAsync(
        AccessScope accessScope,
        CancellationToken cancellationToken = default);

    Task<PagedResult<ParcelTrackingDto>> SearchPageAsync(
        AccessScope accessScope,
        string? barcodeContains,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

public interface IBagStorageService
{
    Task<StorageResult> StoreAsync(BagStoreCommand command, CancellationToken cancellationToken = default);
}

public interface IDispatchStorageService
{
    Task<StorageResult> StoreAsync(
        DispatchStoreCommand command,
        AccessScope accessScope,
        CancellationToken cancellationToken = default);
}

public interface IBagDispatchQueryService
{
    Task<BagDto?> FindBagAsync(
        string bagBarcode,
        AccessScope accessScope,
        CancellationToken cancellationToken = default);

    Task<DispatchDto?> FindDispatchAsync(
        string dispatchId,
        AccessScope accessScope,
        CancellationToken cancellationToken = default);
}

public interface IDeviceManagementService
{
    Task<RegisteredDeviceResult> RegisterAsync(
        RegisterDeviceCommand command,
        CancellationToken cancellationToken = default);

    Task<RegisteredDeviceResult> RotateSecretAsync(
        RotateDeviceSecretCommand command,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(UpdateDeviceCommand command, CancellationToken cancellationToken = default);

    Task SetActivationAsync(
        Guid deviceId,
        bool active,
        string idempotencyKey,
        string actor,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public interface IDeviceQueryService
{
    Task<IReadOnlyCollection<DeviceDto>> ListAsync(
        AccessScope accessScope,
        string? exchangeCenterCode = null,
        CancellationToken cancellationToken = default);

    Task<PagedResult<DeviceDto>> ListPageAsync(
        AccessScope accessScope,
        string? exchangeCenterCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

public interface IConfigurationService
{
    Task<ConfigurationSnapshotDto> PublishAsync(
        PublishConfigurationCommand command,
        CancellationToken cancellationToken = default);

    Task<ConfigurationSnapshotDto> GetBootstrapAsync(
        string exchangeCenterCode,
        CancellationToken cancellationToken = default);
}

public interface IConfigurationQueryService
{
    Task<ConfigurationSnapshotDto?> GetCurrentAsync(
        string exchangeCenterCode,
        AccessScope accessScope,
        CancellationToken cancellationToken = default);
}

public interface IPresignedUrlService
{
    PresignedUrlResult Create(PresignedUrlCommand command);
}

public interface IEdgeHealthService
{
    Task RegisterAsync(RegisterEdgeCommand command, CancellationToken cancellationToken = default);

    Task RecordHeartbeatAsync(EdgeHeartbeatCommand command, CancellationToken cancellationToken = default);

    Task RecordEventAsync(RecordOperationalEventCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<EdgeHealthDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<PagedResult<EdgeHealthDto>> ListPageAsync(
        AccessScope accessScope,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<EdgeHealthDto?> FindAsync(Guid edgeId, CancellationToken cancellationToken = default);
}

public interface IEdgeHealthMonitor
{
    Task<int> MarkStaleEdgesOfflineAsync(
        TimeSpan threshold,
        CancellationToken cancellationToken = default);
}

public interface IOperationalResultService
{
    Task<StorageResult> StoreAsync(
        OperationalResultCommand command,
        CancellationToken cancellationToken = default);
}

public interface IEdgeAuthService
{
    Task<ProvisionedEdgeCredentialResult> ProvisionAsync(
        ProvisionEdgeCredentialCommand command,
        CancellationToken cancellationToken = default);

    Task<ProvisionedEdgeCredentialResult> RotateSecretAsync(
        RotateEdgeCredentialCommand command,
        CancellationToken cancellationToken = default);

    Task<AccessTokenResult> AuthenticateAsync(
        AuthenticateEdgeCommand command,
        CancellationToken cancellationToken = default);

    Task DisableAsync(
        Guid deviceId,
        string idempotencyKey,
        string actor,
        string correlationId,
        CancellationToken cancellationToken = default);
}

public interface IAdminAuthService
{
    Task<AccessTokenResult> AuthenticateAsync(
        AuthenticateAdminCommand command,
        CancellationToken cancellationToken = default);
}

public interface IAdminProvisioningService
{
    Task<ProvisionedAdminResult> ProvisionInitialAsync(
        ProvisionInitialAdminCommand command,
        CancellationToken cancellationToken = default);
}

public interface IAuditService
{
    Task RecordAsync(AuditCommand command, CancellationToken cancellationToken = default);
}

public interface IIdempotencyService
{
    IdempotencyDecision Evaluate(IdempotencyRecord? existing, string requestHash);
}

public interface ICorrelationService
{
    string GetOrCreate(string? suppliedCorrelationId);
}

public interface IOutboxPublisher
{
    Task<int> PublishBatchAsync(
        int maximumCount,
        int maximumAttempts,
        CancellationToken cancellationToken = default);
}
