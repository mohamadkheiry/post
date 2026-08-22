using Microsoft.EntityFrameworkCore;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Bags;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Infrastructure.Persistence;

internal sealed class BagRepository(EdgeDbContext db) : IEdgeBagRepository
{
    public async Task<EdgeBag?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var row = await db.Bags.SingleOrDefaultAsync(x => x.LocalOperationId == operationId, cancellationToken);
        return row is null ? null : Materialize(row);
    }

    public async Task<EdgeBag?> FindByBarcodeAsync(string bagBarcode, CancellationToken cancellationToken)
    {
        var row = await db.Bags.SingleOrDefaultAsync(x => x.BagBarcode == bagBarcode, cancellationToken);
        return row is null ? null : Materialize(row);
    }

    public async Task<IReadOnlyList<EdgeBag>> FindByBarcodesAsync(
        IReadOnlyCollection<string> bagBarcodes,
        CancellationToken cancellationToken)
    {
        var rows = await db.Bags.Where(x => bagBarcodes.Contains(x.BagBarcode)).ToListAsync(cancellationToken);
        return rows.Select(Materialize).ToArray();
    }

    public async Task<IReadOnlyList<EdgeBag>> ListUndispatchedAsync(
        string deviceId,
        string destinationCode,
        CancellationToken cancellationToken)
    {
        var rows = await db.Bags
            .Where(x => x.DeviceId == deviceId && x.DestinationCode == destinationCode && x.DispatchId == null)
            .ToListAsync(cancellationToken);
        return rows.OrderBy(x => x.CreatedAt).Select(Materialize).ToArray();
    }

    public void Add(EdgeBag bag)
    {
        ArgumentNullException.ThrowIfNull(bag);
        db.Bags.Add(ToRow(bag));
    }

    private EdgeBag Materialize(BagRow row)
    {
        var bag = EdgeBag.Rehydrate(
            row.BagBarcode,
            row.LocalOperationId,
            row.DeviceId,
            row.DestinationCode,
            PersistenceJson.Deserialize<string[]>(row.MemberBarcodesJson),
            row.Label,
            row.SealNumber,
            row.TransportType,
            new BagCounts(row.N, row.P, row.M, row.Q),
            PersistenceJson.Deserialize<ParcelError[]>(row.ErrorsJson),
            row.CreatedAt,
            row.DispatchId,
            row.CoreConfirmed);
        db.TrackSynchronizer(() => Copy(bag, row));
        return bag;
    }

    private static BagRow ToRow(EdgeBag bag)
    {
        var row = new BagRow { BagBarcode = bag.BagBarcode };
        Copy(bag, row);
        return row;
    }

    private static void Copy(EdgeBag bag, BagRow row)
    {
        row.LocalOperationId = bag.LocalOperationId;
        row.DeviceId = bag.DeviceId;
        row.DestinationCode = bag.DestinationCenterCode;
        row.MemberBarcodesJson = PersistenceJson.Serialize(bag.MemberBarcodes);
        row.Label = bag.BagLabel.ToArray();
        row.SealNumber = bag.SealNumber;
        row.TransportType = bag.TransportType;
        row.N = bag.Counts.N;
        row.P = bag.Counts.P;
        row.M = bag.Counts.M;
        row.Q = bag.Counts.Q;
        row.ErrorsJson = PersistenceJson.Serialize(bag.Errors);
        row.CreatedAt = bag.CreatedAt;
        row.DispatchId = bag.DispatchId;
        row.CoreConfirmed = bag.CoreConfirmed;
    }
}

internal sealed class PendingBagClosureRepository(EdgeDbContext db) : IPendingBagClosureRepository
{
    public async Task<PendingBagClosure?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var row = await db.PendingBagClosures.SingleOrDefaultAsync(x => x.LocalOperationId == operationId, cancellationToken);
        return row is null ? null : Materialize(row);
    }

    public async Task<IReadOnlyList<PendingBagClosure>> ListDueAsync(
        DateTimeOffset now,
        int take,
        CancellationToken cancellationToken)
    {
        var rows = await db.PendingBagClosures
            .Where(x => x.State == (int)PendingBagClosureState.Pending && x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt)
            .Take(take)
            .ToListAsync(cancellationToken);
        return rows.Select(Materialize).ToArray();
    }

    public void Add(PendingBagClosure closure)
    {
        ArgumentNullException.ThrowIfNull(closure);
        var row = new PendingBagClosureRow { LocalOperationId = closure.LocalOperationId };
        Copy(closure, row);
        db.PendingBagClosures.Add(row);
    }

    private PendingBagClosure Materialize(PendingBagClosureRow row)
    {
        var closure = PendingBagClosure.Rehydrate(
            row.LocalOperationId,
            row.DeviceId,
            row.DestinationCode,
            PersistenceJson.Deserialize<Guid[]>(row.ParcelReadIdsJson),
            row.SealNumber,
            row.TransportType,
            row.CreatedAt,
            (PendingBagClosureState)row.State,
            row.Attempts,
            row.NextAttemptAt,
            row.LastError,
            row.FinalBagBarcode);
        db.TrackSynchronizer(() => Copy(closure, row));
        return closure;
    }

    private static void Copy(PendingBagClosure closure, PendingBagClosureRow row)
    {
        row.DeviceId = closure.DeviceId;
        row.DestinationCode = closure.DestinationCenterCode;
        row.ParcelReadIdsJson = PersistenceJson.Serialize(closure.ParcelReadIds);
        row.SealNumber = closure.SealNumber;
        row.TransportType = closure.TransportType;
        row.CreatedAt = closure.CreatedAt;
        row.State = (int)closure.State;
        row.Attempts = closure.Attempts;
        row.NextAttemptAt = closure.NextAttemptAt;
        row.LastError = closure.LastError;
        row.FinalBagBarcode = closure.FinalBagBarcode;
    }
}
