using Post.Edge.Domain.Bags;
using Post.Edge.Domain.Devices;
using Post.Edge.Domain.Dispatches;
using Post.Edge.Domain.Parcels;

namespace Post.Edge.Application.Ports;

public interface IEdgeParcelRepository
{
    Task<EdgeParcel?> FindByReadIdAsync(Guid readId, CancellationToken cancellationToken);
    Task<EdgeParcel?> FindByCanonicalBarcodeAsync(string canonicalBarcode, CancellationToken cancellationToken);
    Task<IReadOnlyList<EdgeParcel>> ListEligibleForBagAsync(
        string deviceId,
        string destinationCode,
        DateTimeOffset? from,
        DateTimeOffset? until,
        CancellationToken cancellationToken);
    void Add(EdgeParcel parcel);
}

public interface IEdgeBagRepository
{
    Task<EdgeBag?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken);
    Task<EdgeBag?> FindByBarcodeAsync(string bagBarcode, CancellationToken cancellationToken);
    Task<IReadOnlyList<EdgeBag>> FindByBarcodesAsync(
        IReadOnlyCollection<string> bagBarcodes,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<EdgeBag>> ListUndispatchedAsync(
        string deviceId,
        string destinationCode,
        CancellationToken cancellationToken);
    void Add(EdgeBag bag);
}

public interface IPendingBagClosureRepository
{
    Task<PendingBagClosure?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingBagClosure>> ListDueAsync(
        DateTimeOffset now,
        int take,
        CancellationToken cancellationToken);
    void Add(PendingBagClosure closure);
}

public interface IDispatchRepository
{
    Task<DispatchRecord?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken);
    void Add(DispatchRecord dispatch);
}

public sealed record PendingInboundItem(
    Guid OperationId,
    Guid ReadId,
    string PostalTrackingId,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    string? LastError);

public interface IPendingInboundRepository
{
    Task<PendingInboundItem?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingInboundItem>> ListDueAsync(DateTimeOffset now, int take, CancellationToken cancellationToken);
    void Add(PendingInboundItem item);
    void Update(PendingInboundItem item);
    void Remove(PendingInboundItem item);
}

public sealed record InboundImageContent(
    string ImageId,
    byte[] Content,
    string? MimeType,
    string? Description);

public sealed record InboundEvidence(
    Guid ReadId,
    string? SupplementaryDataJson,
    IReadOnlyCollection<InboundImageContent> Images,
    DateTimeOffset CreatedAt);

public interface IInboundEvidenceRepository
{
    void Add(InboundEvidence evidence);
    Task<InboundImageContent?> FindImageAsync(Guid readId, string imageId, CancellationToken cancellationToken);
    Task<string?> FindSupplementaryDataAsync(Guid readId, CancellationToken cancellationToken);
}

public interface IDeviceConfigurationRepository
{
    Task<ConfigurationSnapshot?> GetAsync(CancellationToken cancellationToken);
    Task ReplaceAsync(ConfigurationSnapshot snapshot, CancellationToken cancellationToken);
}

public interface IDeviceSessionStore
{
    Task SaveAsync(DeviceSession session, CancellationToken cancellationToken);
    Task<DeviceSession?> FindByConnectionAsync(string connectionId, CancellationToken cancellationToken);
    Task RemoveAsync(string connectionId, CancellationToken cancellationToken);
}

public interface IDeviceNetworkBindingRepository
{
    Task<IReadOnlyList<DeviceNetworkBinding>> ListAsync(string deviceId, CancellationToken cancellationToken);
    Task ReplaceAsync(string deviceId, IReadOnlyCollection<string> ipAddresses, CancellationToken cancellationToken);
}

public interface IDeviceNetworkPolicy
{
    bool AllowDeviceWithoutBinding { get; }
}

public interface ILocalAdministratorRepository
{
    Task<bool> ExistsAsync(CancellationToken cancellationToken);
    Task<LocalAdministrator?> GetAsync(CancellationToken cancellationToken);
    Task StoreAsync(LocalAdministrator administrator, CancellationToken cancellationToken);
    Task<LocalAdministratorSession?> FindSessionAsync(string tokenHash, CancellationToken cancellationToken);
    void AddSession(LocalAdministratorSession session);
    Task RemoveSessionAsync(string tokenHash, CancellationToken cancellationToken);
    Task RemoveAllSessionsAsync(CancellationToken cancellationToken);
    Task RemoveExpiredSessionsAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IOperationJournal
{
    Task<TResponse?> GetResponseAsync<TResponse>(
        Guid operationId,
        string operationType,
        CancellationToken cancellationToken);
    Task<OperationJournalEntry<TResponse>?> GetAsync<TResponse>(
        Guid operationId,
        string operationType,
        CancellationToken cancellationToken);
    void AddResponse<TResponse>(
        Guid operationId,
        string operationType,
        TResponse response,
        DateTimeOffset createdAt,
        string? requestHash = null);
}

public sealed record ExternalOperationSaga(
    Guid OperationId,
    string OperationType,
    string RequestHash,
    string IntentJson,
    string? ReceiptJson,
    bool Finalized,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface IExternalOperationSagaRepository
{
    Task<ExternalOperationSaga?> GetAsync(
        Guid operationId,
        string operationType,
        CancellationToken cancellationToken);
    void AddIntent(ExternalOperationSaga saga);
    Task StoreReceiptAsync(
        Guid operationId,
        string operationType,
        string receiptJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
    Task MarkFinalizedAsync(
        Guid operationId,
        string operationType,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
    Task<ExternalOperationSagaStatus> GetStatusAsync(CancellationToken cancellationToken);
}

public sealed record ExternalOperationSagaStatus(int UnresolvedCount, DateTimeOffset? OldestCreatedAt);

public sealed record OperationJournalEntry<TResponse>(string RequestHash, TResponse Response);

public sealed record OutboxMessage(
    Guid MessageId,
    string Target,
    string MessageType,
    string PayloadJson,
    string IdempotencyKey,
    DateTimeOffset OccurredAt,
    DateTimeOffset NextAttemptAt);

public interface IOutboxWriter
{
    void Enqueue(OutboxMessage message);
}

public interface IOutboxStatus
{
    Task<bool> IsPendingAsync(string idempotencyKey, CancellationToken cancellationToken);
}

public interface IEdgeUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

public interface IDestinationLock : IAsyncDisposable;

public interface IDestinationLockProvider
{
    ValueTask<IDestinationLock> AcquireAsync(
        string deviceId,
        string destinationCode,
        CancellationToken cancellationToken);
}
