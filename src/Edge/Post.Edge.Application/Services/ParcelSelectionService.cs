using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Common;
using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Application.Services;

public sealed class ParcelSelectionService(IEdgeParcelRepository parcels) : IParcelSelectionService
{
    public async Task<IReadOnlyList<SelectedParcel>> SelectAsync(
        ParcelSelectionFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var destination = PostalCenterCode.Create(filter.DestinationCode).Value;
        var candidates = (await parcels.ListEligibleForBagAsync(
                filter.DeviceId,
                destination,
                filter.From,
                filter.To,
                cancellationToken))
            .Where(x => x.State is EdgeParcelState.DestinationAssigned or EdgeParcelState.Deferred)
            .Where(x => filter.ChuteIds is null || filter.ChuteIds.Count == 0 ||
                        x.ChuteId is not null && filter.ChuteIds.Contains(x.ChuteId, StringComparer.OrdinalIgnoreCase))
            .Where(x => filter.ParcelTypes is null || filter.ParcelTypes.Count == 0 ||
                        x.ParcelType is not null && filter.ParcelTypes.Contains(x.ParcelType, StringComparer.OrdinalIgnoreCase))
            .Where(x => filter.ServiceTypes is null || filter.ServiceTypes.Count == 0 ||
                        x.ServiceType is not null && filter.ServiceTypes.Contains(x.ServiceType, StringComparer.OrdinalIgnoreCase))
            .OrderBy(x => x.ReadTimestamp)
            .ThenBy(x => x.ReadId)
            .GroupBy(x => x.CanonicalBarcode, StringComparer.Ordinal)
            .Select(x => x.Last())
            .OrderBy(x => x.ReadTimestamp)
            .ThenBy(x => x.ReadId)
            .ToList();

        // Protocol count is informational. It must never change the selected set.
        if (filter.LastReadId is not null || !string.IsNullOrWhiteSpace(filter.LastCanonicalBarcode))
        {
            var matchingIndexes = new List<int>(2);
            if (filter.LastReadId is { } lastReadId)
            {
                matchingIndexes.Add(candidates.FindIndex(x => x.ReadId == lastReadId));
            }

            if (!string.IsNullOrWhiteSpace(filter.LastCanonicalBarcode))
            {
                var canonical = Barcode.Parse(filter.LastCanonicalBarcode).Canonical;
                matchingIndexes.Add(candidates.FindIndex(x => x.CanonicalBarcode == canonical));
            }

            if (matchingIndexes.Any(x => x < 0) || matchingIndexes.Distinct().Count() > 1)
            {
                throw new DomainException("The requested last parcel is not an unambiguous member of the eligible set.");
            }

            candidates = candidates.Take(matchingIndexes[0] + 1).ToList();
        }

        return candidates.Select(x => new SelectedParcel(
            x.ReadId,
            x.CanonicalBarcode,
            x.ReadTimestamp,
            x.State,
            x.PhysicalAttributes.WeightGrams)).ToArray();
    }
}
