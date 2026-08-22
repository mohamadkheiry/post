using Post.Core.Domain.Devices;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Operations;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.Security;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Application.Abstractions;

public sealed record PersistencePage<T>(IReadOnlyCollection<T> Items, int TotalCount);

public interface IParcelLifecycleStore
{
    Task<ParcelDossier?> FindParcelAsync(ParcelBarcode barcode, CancellationToken cancellationToken = default);

    Task<ReadingRecord?> FindReadingAsync(Guid readingRecordId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ParcelDossier>> FindParcelsAsync(
        IReadOnlyCollection<ParcelBarcode> barcodes,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<ParcelDossier>> QueryParcelsAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        CancellationToken cancellationToken = default);

    Task<PersistencePage<ParcelDossier>> QueryParcelsPageAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        string? barcodeContains,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    Task<Bag?> FindBagAsync(BagBarcode barcode, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<Bag>> FindBagsAsync(
        IReadOnlyCollection<BagBarcode> barcodes,
        CancellationToken cancellationToken = default);

    Task<Dispatch?> FindDispatchAsync(DispatchId id, CancellationToken cancellationToken = default);

    Task<IdempotencyRecord?> FindIdempotencyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default);

    Task<InboxMessage?> FindInboxMessageAsync(Guid eventId, CancellationToken cancellationToken = default);

    Task<PendingAttachment?> FindPendingAttachmentAsync(
        Guid readingRecordId,
        string objectKey,
        CancellationToken cancellationToken = default);

    Task<PendingAttachment?> FindPendingAttachmentByEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<PendingAttachment>> GetDuePendingAttachmentsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default);

    void AddParcel(ParcelDossier dossier);

    void AddReading(ReadingRecord reading);

    void AddAttachment(SupplementaryAttachment attachment);

    void AddBag(Bag bag);

    void AddDispatch(Dispatch dispatch);

    void AddPendingAttachment(PendingAttachment attachment);

    void RemovePendingAttachment(PendingAttachment attachment);

    void AddIdempotency(IdempotencyRecord record);

    void AddInboxMessage(InboxMessage message);

    void AddOutbox(OutboxMessage message);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IDeviceConfigStore
{
    Task<IdempotencyRecord?> FindIdempotencyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default);

    Task<SortingDevice?> FindDeviceAsync(DeviceId id, CancellationToken cancellationToken = default);

    Task<SortingDevice?> FindDeviceByLogicalCodeAsync(
        LogicalDeviceCode logicalCode,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SortingDevice>> ListDevicesAsync(
        ExchangeCenterCode center,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<SortingDevice>> QueryDevicesAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        CancellationToken cancellationToken = default);

    Task<PersistencePage<SortingDevice>> QueryDevicesPageAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    Task<ConfigurationSnapshot?> GetLatestSnapshotAsync(
        ExchangeCenterCode center,
        CancellationToken cancellationToken = default);

    void AddDevice(SortingDevice device);

    void AddSnapshot(ConfigurationSnapshot snapshot);

    void AddIdempotency(IdempotencyRecord record);

    void AddOutbox(OutboxMessage message);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IEdgeHealthStore
{
    Task<IdempotencyRecord?> FindIdempotencyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default);

    Task<EdgeHealthStatus?> FindAsync(EdgeId edgeId, CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<EdgeHealthStatus>> ListAsync(CancellationToken cancellationToken = default);

    Task<PersistencePage<EdgeHealthStatus>> QueryPageAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    void AddStatus(EdgeHealthStatus status);

    void AddEvent(OperationalEvent operationalEvent);

    void AddIdempotency(IdempotencyRecord record);

    void AddOutbox(OutboxMessage message);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IOperationalResultStore
{
    Task<OperationalResult?> FindByIdempotencyKeyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default);

    void AddResult(OperationalResult result);

    void AddIdempotency(IdempotencyRecord record);

    void AddOutbox(OutboxMessage message);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IIdentityStore
{
    Task<IdempotencyRecord?> FindIdempotencyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default);

    Task<EdgeCredential?> FindEdgeCredentialAsync(
        LogicalDeviceCode logicalCode,
        CancellationToken cancellationToken = default);

    Task<EdgeCredential?> FindEdgeCredentialAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default);

    void AddEdgeCredential(EdgeCredential credential);

    void AddIdempotency(IdempotencyRecord record);

    void AddAudit(AuditRecord auditRecord);

    void AddOutbox(OutboxMessage message);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IOutboxStore
{
    Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
