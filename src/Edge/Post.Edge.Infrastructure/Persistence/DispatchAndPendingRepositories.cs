using Microsoft.EntityFrameworkCore;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Dispatches;

namespace Post.Edge.Infrastructure.Persistence;

internal sealed class DispatchRepository(EdgeDbContext db) : IDispatchRepository
{
    public async Task<DispatchRecord?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var row = await db.Dispatches.SingleOrDefaultAsync(x => x.LocalOperationId == operationId, cancellationToken);
        if (row is null) return null;
        var dispatch = DispatchRecord.Rehydrate(
            row.LocalOperationId,
            row.DispatchId,
            row.DeviceId,
            row.DestinationCode,
            PersistenceJson.Deserialize<string[]>(row.BagBarcodesJson),
            row.CreatedAt,
            row.CoreConfirmed);
        db.TrackSynchronizer(() => row.CoreConfirmed = dispatch.CoreConfirmed);
        return dispatch;
    }

    public void Add(DispatchRecord dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        db.Dispatches.Add(new DispatchRow
        {
            LocalOperationId = dispatch.LocalOperationId,
            DispatchId = dispatch.DispatchId,
            DeviceId = dispatch.DeviceId,
            DestinationCode = dispatch.DestinationCenterCode,
            BagBarcodesJson = PersistenceJson.Serialize(dispatch.BagBarcodes),
            CreatedAt = dispatch.CreatedAt,
            CoreConfirmed = dispatch.CoreConfirmed
        });
    }
}

internal sealed class PendingInboundRepository(EdgeDbContext db) : IPendingInboundRepository
{
    private readonly Dictionary<Guid, PendingInboundRow> _loaded = [];

    public async Task<PendingInboundItem?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var row = await db.PendingInbounds.SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
        if (row is null) return null;
        _loaded[row.OperationId] = row;
        return ToModel(row);
    }

    public async Task<IReadOnlyList<PendingInboundItem>> ListDueAsync(
        DateTimeOffset now,
        int take,
        CancellationToken cancellationToken)
    {
        var rows = await db.PendingInbounds
            .Where(x => x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .Take(take)
            .ToListAsync(cancellationToken);
        foreach (var row in rows) _loaded[row.OperationId] = row;
        return rows.Select(ToModel).ToArray();
    }

    public void Add(PendingInboundItem item) => db.PendingInbounds.Add(ToRow(item));

    public void Update(PendingInboundItem item)
    {
        if (!_loaded.TryGetValue(item.OperationId, out var row))
        {
            row = db.PendingInbounds.Local.SingleOrDefault(x => x.OperationId == item.OperationId)
                  ?? throw new InvalidOperationException("Pending inbound item must be loaded before it is updated.");
        }
        Copy(item, row);
    }

    public void Remove(PendingInboundItem item)
    {
        if (!_loaded.TryGetValue(item.OperationId, out var row))
        {
            row = db.PendingInbounds.Local.SingleOrDefault(x => x.OperationId == item.OperationId)
                  ?? throw new InvalidOperationException("Pending inbound item must be loaded before it is removed.");
        }
        db.PendingInbounds.Remove(row);
    }

    private static PendingInboundItem ToModel(PendingInboundRow row) => new(
        row.OperationId, row.ReadId, row.PostalTrackingId, row.Attempts, row.NextAttemptAt, row.LastError);

    private static PendingInboundRow ToRow(PendingInboundItem item)
    {
        var row = new PendingInboundRow { OperationId = item.OperationId };
        Copy(item, row);
        return row;
    }

    private static void Copy(PendingInboundItem item, PendingInboundRow row)
    {
        row.ReadId = item.ReadId;
        row.PostalTrackingId = item.PostalTrackingId;
        row.Attempts = item.Attempts;
        row.NextAttemptAt = item.NextAttemptAt;
        row.LastError = item.LastError;
    }
}
