using Microsoft.EntityFrameworkCore;
using Post.Core.Application.Abstractions;
using Post.Core.Domain.Common;
using Post.Core.Domain.Devices;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Operations;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.Security;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Infrastructure.Persistence;

public sealed class EfParcelLifecycleStore(ParcelLifecycleDbContext dbContext) : IParcelLifecycleStore
{
    public Task<ParcelDossier?> FindParcelAsync(
        ParcelBarcode barcode,
        CancellationToken cancellationToken = default) =>
        dbContext.ParcelDossiers
            .Include(x => x.Readings)
            .ThenInclude(x => x.Discrepancies)
            .Include(x => x.Readings)
            .ThenInclude(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.Barcode == barcode, cancellationToken);

    public Task<ReadingRecord?> FindReadingAsync(
        Guid readingRecordId,
        CancellationToken cancellationToken = default) =>
        dbContext.ReadingRecords
            .Include(x => x.Discrepancies)
            .Include(x => x.Attachments)
            .SingleOrDefaultAsync(x => x.Id == readingRecordId, cancellationToken);

    public async Task<IReadOnlyCollection<ParcelDossier>> FindParcelsAsync(
        IReadOnlyCollection<ParcelBarcode> barcodes,
        CancellationToken cancellationToken = default) =>
        await dbContext.ParcelDossiers
            .Include(x => x.Readings)
            .Where(x => barcodes.Contains(x.Barcode))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyCollection<ParcelDossier>> QueryParcelsAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        CancellationToken cancellationToken = default)
    {
        IQueryable<ParcelDossier> query = dbContext.ParcelDossiers.Include(x => x.Readings);
        if (allowedCenters is not null)
        {
            query = query.Where(x =>
                allowedCenters.Contains(x.OriginCode) ||
                allowedCenters.Contains(x.DestinationCode) ||
                x.Readings.Any(r => allowedCenters.Contains(r.ExchangeCenterCode)));
        }

        return await query.AsNoTracking().ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PersistencePage<ParcelDossier>> QueryParcelsPageAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        string? barcodeContains,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(take, 0);
        IQueryable<ParcelDossier> query;
        if (string.IsNullOrWhiteSpace(barcodeContains))
        {
            query = dbContext.ParcelDossiers.AsNoTracking();
        }
        else
        {
            var escaped = barcodeContains
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal)
                .Replace("[", "\\[", StringComparison.Ordinal);
            var pattern = string.Concat("%", escaped, "%");
            query = dbContext.ParcelDossiers
                .FromSqlInterpolated($"SELECT * FROM [ParcelDossiers] WHERE [Barcode] LIKE {pattern} ESCAPE '\\'")
                .AsNoTracking();
        }

        if (allowedCenters is not null)
        {
            query = query.Where(x =>
                allowedCenters.Contains(x.OriginCode) ||
                allowedCenters.Contains(x.DestinationCode) ||
                x.Readings.Any(reading => allowedCenters.Contains(reading.ExchangeCenterCode)));
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(x => x.Barcode)
            .Skip(skip)
            .Take(take)
            .Include(x => x.Readings)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new PersistencePage<ParcelDossier>(items, totalCount);
    }

    public Task<Bag?> FindBagAsync(BagBarcode barcode, CancellationToken cancellationToken = default) =>
        dbContext.Bags.Include(x => x.Members).SingleOrDefaultAsync(x => x.Barcode == barcode, cancellationToken);

    public async Task<IReadOnlyCollection<Bag>> FindBagsAsync(
        IReadOnlyCollection<BagBarcode> barcodes,
        CancellationToken cancellationToken = default) =>
        await dbContext.Bags
            .Include(x => x.Members)
            .Where(x => barcodes.Contains(x.Barcode))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<Dispatch?> FindDispatchAsync(DispatchId id, CancellationToken cancellationToken = default) =>
        dbContext.Dispatches.Include(x => x.Bags).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<IdempotencyRecord?> FindIdempotencyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default) =>
        dbContext.IdempotencyRecords.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

    public Task<InboxMessage?> FindInboxMessageAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        dbContext.InboxMessages.SingleOrDefaultAsync(x => x.EventId == eventId, cancellationToken);

    public Task<PendingAttachment?> FindPendingAttachmentAsync(
        Guid readingRecordId,
        string objectKey,
        CancellationToken cancellationToken = default) =>
        dbContext.PendingAttachments.SingleOrDefaultAsync(
            x => x.ReadingRecordId == readingRecordId && x.ObjectKey == objectKey,
            cancellationToken);

    public Task<PendingAttachment?> FindPendingAttachmentByEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default) =>
        dbContext.PendingAttachments.SingleOrDefaultAsync(
            x => x.EventId == eventId,
            cancellationToken);

    public async Task<IReadOnlyCollection<PendingAttachment>> GetDuePendingAttachmentsAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), "Pending attachment batch size must be positive.");
        }

        var nowUtcTicks = now.UtcDateTime.Ticks;
        return await dbContext.PendingAttachments
            .Where(x =>
                x.DeadLetteredAtUtcTicks == null &&
                x.NextAttemptAtUtcTicks != null &&
                x.NextAttemptAtUtcTicks <= nowUtcTicks)
            .OrderBy(x => x.NextAttemptAtUtcTicks)
            .ThenBy(x => x.CreatedAtUtcTicks)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public void AddParcel(ParcelDossier dossier) => dbContext.ParcelDossiers.Add(dossier);

    public void AddReading(ReadingRecord reading) => dbContext.ReadingRecords.Add(reading);

    public void AddAttachment(SupplementaryAttachment attachment) =>
        dbContext.SupplementaryAttachments.Add(attachment);

    public void AddBag(Bag bag) => dbContext.Bags.Add(bag);

    public void AddDispatch(Dispatch dispatch) => dbContext.Dispatches.Add(dispatch);

    public void AddPendingAttachment(PendingAttachment attachment) => dbContext.PendingAttachments.Add(attachment);

    public void RemovePendingAttachment(PendingAttachment attachment) =>
        dbContext.PendingAttachments.Remove(attachment);

    public void AddIdempotency(IdempotencyRecord record) => dbContext.IdempotencyRecords.Add(record);

    public void AddInboxMessage(InboxMessage message) => dbContext.InboxMessages.Add(message);

    public void AddOutbox(OutboxMessage message) => dbContext.OutboxMessages.Add(message);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => await RelationalConstraintClassifier.SaveChangesAsync(
            dbContext,
            "ParcelLifecycle",
            cancellationToken).ConfigureAwait(false);
}

public sealed class EfDeviceConfigStore(DeviceConfigDbContext dbContext) : IDeviceConfigStore
{
    public Task<IdempotencyRecord?> FindIdempotencyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default) =>
        dbContext.IdempotencyRecords.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

    public Task<SortingDevice?> FindDeviceAsync(DeviceId id, CancellationToken cancellationToken = default) =>
        dbContext.SortingDevices.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<SortingDevice?> FindDeviceByLogicalCodeAsync(
        LogicalDeviceCode logicalCode,
        CancellationToken cancellationToken = default) =>
        dbContext.SortingDevices.SingleOrDefaultAsync(x => x.LogicalCode == logicalCode, cancellationToken);

    public async Task<IReadOnlyCollection<SortingDevice>> ListDevicesAsync(
        ExchangeCenterCode center,
        CancellationToken cancellationToken = default) =>
        await dbContext.SortingDevices
            .Where(x => x.ExchangeCenterCode == center)
            .OrderBy(x => x.LogicalCode)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyCollection<SortingDevice>> QueryDevicesAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        CancellationToken cancellationToken = default)
    {
        IQueryable<SortingDevice> query = dbContext.SortingDevices;
        if (allowedCenters is not null)
        {
            query = query.Where(x => allowedCenters.Contains(x.ExchangeCenterCode));
        }

        return await query
            .OrderBy(x => x.ExchangeCenterCode)
            .ThenBy(x => x.LogicalCode)
            .AsNoTracking()
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PersistencePage<SortingDevice>> QueryDevicesPageAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(take, 0);
        IQueryable<SortingDevice> query = dbContext.SortingDevices.AsNoTracking();
        if (allowedCenters is not null)
        {
            query = query.Where(x => allowedCenters.Contains(x.ExchangeCenterCode));
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(x => x.ExchangeCenterCode)
            .ThenBy(x => x.LogicalCode)
            .Skip(skip)
            .Take(take)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new PersistencePage<SortingDevice>(items, totalCount);
    }

    public Task<ConfigurationSnapshot?> GetLatestSnapshotAsync(
        ExchangeCenterCode center,
        CancellationToken cancellationToken = default) =>
        dbContext.ConfigurationSnapshots
            .Include(x => x.Devices)
            .Include(x => x.PostalCodes)
            .Where(x => x.ExchangeCenterCode == center)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public void AddDevice(SortingDevice device) => dbContext.SortingDevices.Add(device);

    public void AddSnapshot(ConfigurationSnapshot snapshot) => dbContext.ConfigurationSnapshots.Add(snapshot);

    public void AddIdempotency(IdempotencyRecord record) => dbContext.IdempotencyRecords.Add(record);

    public void AddOutbox(OutboxMessage message) => dbContext.OutboxMessages.Add(message);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => await RelationalConstraintClassifier.SaveChangesAsync(
            dbContext,
            "DeviceConfig",
            cancellationToken).ConfigureAwait(false);
}

public sealed class EfEdgeHealthStore(EdgeHealthDbContext dbContext) : IEdgeHealthStore
{
    public Task<IdempotencyRecord?> FindIdempotencyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default) =>
        dbContext.IdempotencyRecords.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

    public Task<EdgeHealthStatus?> FindAsync(EdgeId edgeId, CancellationToken cancellationToken = default) =>
        dbContext.EdgeHealthStatuses.SingleOrDefaultAsync(x => x.EdgeId == edgeId, cancellationToken);

    public async Task<IReadOnlyCollection<EdgeHealthStatus>> ListAsync(CancellationToken cancellationToken = default) =>
        await dbContext.EdgeHealthStatuses.OrderBy(x => x.ExchangeCenterCode).ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<PersistencePage<EdgeHealthStatus>> QueryPageAsync(
        IReadOnlyCollection<ExchangeCenterCode>? allowedCenters,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(take, 0);
        IQueryable<EdgeHealthStatus> query = dbContext.EdgeHealthStatuses.AsNoTracking();
        if (allowedCenters is not null)
        {
            query = query.Where(x => allowedCenters.Contains(x.ExchangeCenterCode));
        }

        var totalCount = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query
            .OrderBy(x => x.ExchangeCenterCode)
            .ThenBy(x => x.EdgeId)
            .Skip(skip)
            .Take(take)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return new PersistencePage<EdgeHealthStatus>(items, totalCount);
    }

    public void AddStatus(EdgeHealthStatus status) => dbContext.EdgeHealthStatuses.Add(status);

    public void AddEvent(OperationalEvent operationalEvent) => dbContext.OperationalEvents.Add(operationalEvent);

    public void AddIdempotency(IdempotencyRecord record) => dbContext.IdempotencyRecords.Add(record);

    public void AddOutbox(OutboxMessage message) => dbContext.OutboxMessages.Add(message);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => await RelationalConstraintClassifier.SaveChangesAsync(
            dbContext,
            "EdgeHealth",
            cancellationToken).ConfigureAwait(false);
}

public sealed class EfOperationalResultStore(OperationalResultDbContext dbContext) : IOperationalResultStore
{
    public Task<OperationalResult?> FindByIdempotencyKeyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default) =>
        dbContext.OperationalResults.SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);

    public void AddResult(OperationalResult result) => dbContext.OperationalResults.Add(result);

    public void AddIdempotency(IdempotencyRecord record) => dbContext.IdempotencyRecords.Add(record);

    public void AddOutbox(OutboxMessage message) => dbContext.OutboxMessages.Add(message);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => await RelationalConstraintClassifier.SaveChangesAsync(
            dbContext,
            "OperationalResult",
            cancellationToken).ConfigureAwait(false);
}

public sealed class EfIdentityStore(IdentityDbContext dbContext) : IIdentityStore
{
    public Task<IdempotencyRecord?> FindIdempotencyAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken = default) =>
        dbContext.IdempotencyRecords.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);

    public Task<EdgeCredential?> FindEdgeCredentialAsync(
        LogicalDeviceCode logicalCode,
        CancellationToken cancellationToken = default) =>
        dbContext.EdgeCredentials.SingleOrDefaultAsync(x => x.LogicalDeviceCode == logicalCode, cancellationToken);

    public Task<EdgeCredential?> FindEdgeCredentialAsync(
        DeviceId deviceId,
        CancellationToken cancellationToken = default) =>
        dbContext.EdgeCredentials.SingleOrDefaultAsync(x => x.DeviceId == deviceId, cancellationToken);

    public void AddEdgeCredential(EdgeCredential credential) => dbContext.EdgeCredentials.Add(credential);

    public void AddIdempotency(IdempotencyRecord record) => dbContext.IdempotencyRecords.Add(record);

    public void AddAudit(AuditRecord auditRecord) => dbContext.AuditRecords.Add(auditRecord);

    public void AddOutbox(OutboxMessage message) => dbContext.OutboxMessages.Add(message);

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
        => await RelationalConstraintClassifier.SaveChangesAsync(
            dbContext,
            "Identity",
            cancellationToken).ConfigureAwait(false);
}

public sealed class CompositeEfOutboxStore(
    ParcelLifecycleDbContext parcelLifecycle,
    DeviceConfigDbContext deviceConfig,
    EdgeHealthDbContext edgeHealth,
    OperationalResultDbContext operationalResult,
    IdentityDbContext identity) : IOutboxStore
{
    public async Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        var perDatabase = Math.Max(1, maximumCount);
        var batches = await Task.WhenAll(
            Pending(parcelLifecycle.OutboxMessages, perDatabase, cancellationToken),
            Pending(deviceConfig.OutboxMessages, perDatabase, cancellationToken),
            Pending(edgeHealth.OutboxMessages, perDatabase, cancellationToken),
            Pending(operationalResult.OutboxMessages, perDatabase, cancellationToken),
            Pending(identity.OutboxMessages, perDatabase, cancellationToken)).ConfigureAwait(false);
        return batches.SelectMany(x => x)
            .OrderBy(x => x.Sequence)
            .ThenBy(x => x.OccurredAt)
            .Take(maximumCount)
            .ToArray();
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await parcelLifecycle.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await deviceConfig.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await edgeHealth.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await operationalResult.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<OutboxMessage[]> Pending(
        DbSet<OutboxMessage> messages,
        int maximumCount,
        CancellationToken cancellationToken) =>
        await messages
            .Where(x => x.Status == OutboxStatus.Pending)
            .OrderBy(x => x.Sequence)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
}

public sealed class EfSingleDatabaseOutboxStore(
    DbContext dbContext,
    DbSet<OutboxMessage> messages) : IOutboxStore
{
    public async Task<IReadOnlyCollection<OutboxMessage>> GetPendingAsync(
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), "Maximum count must be positive.");
        }

        return await messages
            .Where(x => x.Status == OutboxStatus.Pending)
            .OrderBy(x => x.Sequence)
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        dbContext.SaveChangesAsync(cancellationToken);
}
