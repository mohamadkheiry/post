using Microsoft.EntityFrameworkCore;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Infrastructure.Persistence;

internal sealed class ParcelRepository(EdgeDbContext db) : IEdgeParcelRepository
{
    public async Task<EdgeParcel?> FindByReadIdAsync(Guid readId, CancellationToken cancellationToken)
    {
        var row = await db.Parcels.SingleOrDefaultAsync(x => x.ReadId == readId, cancellationToken);
        return row is null ? null : Materialize(row);
    }

    public async Task<EdgeParcel?> FindByCanonicalBarcodeAsync(string canonicalBarcode, CancellationToken cancellationToken)
    {
        var row = await db.Parcels
            .Where(x => x.CanonicalBarcode == canonicalBarcode)
            .OrderByDescending(x => x.ReadTimestamp)
            .ThenByDescending(x => x.ReadId)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : Materialize(row);
    }

    public async Task<IReadOnlyList<EdgeParcel>> ListEligibleForBagAsync(
        string deviceId,
        string destinationCode,
        DateTimeOffset? from,
        DateTimeOffset? until,
        CancellationToken cancellationToken)
    {
        var query = db.Parcels.Where(x =>
            x.DeviceId == deviceId &&
            x.DestinationCode == destinationCode &&
            (x.State == (int)EdgeParcelState.DestinationAssigned || x.State == (int)EdgeParcelState.Deferred));
        if (from is not null) query = query.Where(x => x.ReadTimestamp >= from.Value);
        if (until is not null) query = query.Where(x => x.ReadTimestamp <= until.Value);
        var rows = await query.OrderBy(x => x.ReadTimestamp).ThenBy(x => x.ReadId).ToListAsync(cancellationToken);
        return rows.Select(Materialize).ToArray();
    }

    public void Add(EdgeParcel parcel)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        db.Parcels.Add(ToRow(parcel));
    }

    private EdgeParcel Materialize(ParcelRow row)
    {
        var dimensions = row.LengthMm is not null
            ? new ParcelDimensions(row.LengthMm.Value, row.WidthMm!.Value, row.HeightMm!.Value)
            : null;
        var parcel = EdgeParcel.Rehydrate(
            row.ReadId,
            row.CanonicalBarcode,
            PersistenceJson.Deserialize<string[]>(row.AliasesJson),
            row.DeviceId,
            row.ReadTimestamp,
            new PhysicalAttributes(row.WeightGrams, dimensions),
            row.ParcelType,
            row.ServiceType,
            (EdgeParcelState)row.State,
            row.DestinationCode,
            row.ChuteId,
            row.PreviousReadId,
            row.BagBarcode,
            PersistenceJson.Deserialize<ParcelError[]>(row.ErrorsJson),
            row.CoreSynced);
        db.TrackSynchronizer(() => Copy(parcel, row));
        return parcel;
    }

    private static ParcelRow ToRow(EdgeParcel parcel)
    {
        var row = new ParcelRow { ReadId = parcel.ReadId };
        Copy(parcel, row);
        return row;
    }

    private static void Copy(EdgeParcel parcel, ParcelRow row)
    {
        row.CanonicalBarcode = parcel.CanonicalBarcode;
        row.AliasesJson = PersistenceJson.Serialize(parcel.Aliases);
        row.DeviceId = parcel.DeviceId;
        row.ReadTimestamp = parcel.ReadTimestamp;
        row.WeightGrams = parcel.PhysicalAttributes.WeightGrams;
        row.LengthMm = parcel.PhysicalAttributes.Dimensions?.LengthMm;
        row.WidthMm = parcel.PhysicalAttributes.Dimensions?.WidthMm;
        row.HeightMm = parcel.PhysicalAttributes.Dimensions?.HeightMm;
        row.ParcelType = parcel.ParcelType;
        row.ServiceType = parcel.ServiceType;
        row.State = (int)parcel.State;
        row.DestinationCode = parcel.OperationalDestinationCode;
        row.ChuteId = parcel.ChuteId;
        row.PreviousReadId = parcel.PreviousReadId;
        row.BagBarcode = parcel.BagBarcode;
        row.ErrorsJson = PersistenceJson.Serialize(parcel.Errors);
        row.CoreSynced = parcel.CoreSynced;
    }
}
