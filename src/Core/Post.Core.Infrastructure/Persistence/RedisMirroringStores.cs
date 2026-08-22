using Microsoft.EntityFrameworkCore;
using Post.Core.Application.Abstractions;
using Post.Core.Caching;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Operations;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Infrastructure.Persistence;

/// <summary>
/// Keeps Redis as a rebuildable mirror of SQL idempotency rows. Cache contents are
/// never returned as the authoritative record and are written only after the SQL
/// transaction commits successfully.
/// </summary>
public sealed class RedisMirroringParcelLifecycleStore(
    IParcelLifecycleStore inner,
    SqlAuthoritativeIdempotencyCoordinator coordinator) : IParcelLifecycleStore
{
    private readonly Dictionary<string, IdempotencyRecord> _recordsToMirror = new(StringComparer.Ordinal);

    public Task<ParcelDossier?> FindParcelAsync(
        ParcelBarcode barcode,
        CancellationToken cancellationToken = default) =>
        inner.FindParcelAsync(barcode, cancellationToken);

    public Task<ReadingRecord?> FindReadingAsync(
        Guid readingRecordId,
        CancellationToken cancellationToken = default) =>
        inner.FindReadingAsync(readingRecordId, cancellationToken);

    public Task<IReadOnlyCollection<ParcelDossier>> FindParcelsAsync(
        IReadOnlyCollection<ParcelBarcode> barcodes,
        CancellationToken cancellationToken = default) =>
        inner.FindParcelsAsync(barcodes, cancellationToken);

    public Task<IReadOnlyCollection<ParcelDossier>> QueryParcelsAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        CancellationToken cancellationToken = default) =>
        inner.QueryParcelsAsync(allowedCenters, cancellationToken);

    public Task<PersistencePage<ParcelDossier>> QueryParcelsPageAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        string? barcodeContains,
        int skip,
        int take,
        CancellationToken cancellationToken = default) =>
        inner.QueryParcelsPageAsync(allowedCenters, barcodeContains, skip, take, cancellationToken);

    public Task<Bag?> FindBagAsync(
        BagBarcode barcode,
        CancellationToken cancellationToken = default) =>
        inner.FindBagAsync(barcode, cancellationToken);

    public Task<IReadOnlyCollection<Bag>> FindBagsAsync(
        IReadOnlyCollection<BagBarcode> barcodes,
        CancellationToken cancellationToken = default) =>
        inner.FindBagsAsync(barcodes, cancellationToken);

    public Task<Dispatch?> FindDispatchAsync(
        DispatchId id,
        CancellationToken cancellationToken = default) =>
        inner.FindDispatchAsync(id, cancellationToken);

    public async Task<IdempotencyRecord?> FindIdempotencyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default)
    {
        var authoritative = await coordinator.FindAuthoritativeAsync(
                IdempotencyCacheScope.ParcelLifecycle,
                key,
                token => inner.FindIdempotencyAsync(key, token),
                cancellationToken)
            .ConfigureAwait(false);
        Track(authoritative);
        return authoritative;
    }

    public Task<InboxMessage?> FindInboxMessageAsync(
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        inner.FindInboxMessageAsync(eventId, cancellationToken);

    public Task<PendingAttachment?> FindPendingAttachmentAsync(
        Guid readingRecordId,
        string objectKey,
        CancellationToken cancellationToken = default) =>
        inner.FindPendingAttachmentAsync(readingRecordId, objectKey, cancellationToken);

    public Task<PendingAttachment?> FindPendingAttachmentByEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        inner.FindPendingAttachmentByEventAsync(eventId, cancellationToken);

    public Task<IReadOnlyCollection<PendingAttachment>> GetDuePendingAttachmentsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default) =>
        inner.GetDuePendingAttachmentsAsync(now, maximumCount, cancellationToken);

    public void AddParcel(ParcelDossier dossier) => inner.AddParcel(dossier);

    public void AddReading(ReadingRecord reading) => inner.AddReading(reading);

    public void AddAttachment(SupplementaryAttachment attachment) => inner.AddAttachment(attachment);

    public void AddBag(Bag bag) => inner.AddBag(bag);

    public void AddDispatch(Dispatch dispatch) => inner.AddDispatch(dispatch);

    public void AddPendingAttachment(PendingAttachment attachment) => inner.AddPendingAttachment(attachment);

    public void RemovePendingAttachment(PendingAttachment attachment) => inner.RemovePendingAttachment(attachment);

    public void AddIdempotency(IdempotencyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Track(record);
        inner.AddIdempotency(record);
    }

    public void AddInboxMessage(InboxMessage message) => inner.AddInboxMessage(message);

    public void AddOutbox(OutboxMessage message) => inner.AddOutbox(message);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var records = _recordsToMirror.Values.ToArray();
        await coordinator.CommitAndMirrorAsync(
                IdempotencyCacheScope.ParcelLifecycle,
                records,
                inner.SaveChangesAsync,
                cancellationToken)
            .ConfigureAwait(false);
        _recordsToMirror.Clear();
    }

    private void Track(IdempotencyRecord? record)
    {
        if (record is not null)
        {
            _recordsToMirror[record.Key.Value] = record;
        }
    }
}

/// <summary>
/// Mirrors OperationalResult idempotency rows after commit while retaining the
/// operational result and its idempotency row in SQL as the only source of truth.
/// </summary>
public sealed class RedisMirroringOperationalResultStore(
    IOperationalResultStore inner,
    OperationalResultDbContext dbContext,
    SqlAuthoritativeIdempotencyCoordinator coordinator) : IOperationalResultStore
{
    private readonly Dictionary<string, IdempotencyRecord> _recordsToMirror = new(StringComparer.Ordinal);

    public async Task<OperationalResult?> FindByIdempotencyKeyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default)
    {
        var authoritative = await coordinator.FindAuthoritativeAsync(
                IdempotencyCacheScope.OperationalResult,
                key,
                token => dbContext.IdempotencyRecords.SingleOrDefaultAsync(record => record.Key == key, token),
                cancellationToken)
            .ConfigureAwait(false);
        Track(authoritative);

        // The cache is only a hint. The business result is always loaded from SQL.
        return await inner.FindByIdempotencyKeyAsync(key, cancellationToken).ConfigureAwait(false);
    }

    public void AddResult(OperationalResult result) => inner.AddResult(result);

    public void AddIdempotency(IdempotencyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Track(record);
        inner.AddIdempotency(record);
    }

    public void AddOutbox(OutboxMessage message) => inner.AddOutbox(message);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var records = _recordsToMirror.Values.ToArray();
        await coordinator.CommitAndMirrorAsync(
                IdempotencyCacheScope.OperationalResult,
                records,
                inner.SaveChangesAsync,
                cancellationToken)
            .ConfigureAwait(false);
        _recordsToMirror.Clear();
    }

    private void Track(IdempotencyRecord? record)
    {
        if (record is not null)
        {
            _recordsToMirror[record.Key.Value] = record;
        }
    }
}
