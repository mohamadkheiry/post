using System.Text;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Bags;
using Post.Edge.Domain.Common;
using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;
using Post.Contracts;
using Post.Contracts.V1.EdgeCore;

namespace Post.Edge.Application.Services;

public sealed class BagClosingService(
    IParcelSelectionService selection,
    IEdgeParcelRepository parcels,
    IEdgeBagRepository bags,
    IDeviceConfigurationRepository configurations,
    IPostalBagClient postal,
    IBagLabelGenerator labelGenerator,
    IOperationJournal operations,
    IExternalOperationSagaRepository externalOperations,
    IOutboxWriter outbox,
    IEdgeUnitOfWork unitOfWork,
    IDestinationLockProvider locks,
    IClock clock) : IBagClosingService
{
    private const string OperationType = "bag.close";

    public async Task<CloseBagResponse> CloseAsync(
        CloseBagRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = OperationIdentity.Resolve(request.OperationId, request.CorrelationId);
        var requestHash = Hash(request);
        var cached = await operations.GetAsync<CloseBagResponse>(identity.Value, OperationType, cancellationToken);
        if (cached is not null) return ReplayOrConflict(cached, request, identity, requestHash);

        PostalCenterCode destination;
        try
        {
            destination = PostalCenterCode.Create(request.DestinationCode);
        }
        catch (DomainException ex)
        {
            return await StoreAsync(
                Error(request, identity, "INVALID_DESTINATION", ex.Message), requestHash, cancellationToken);
        }

        await using var destinationLock = await locks.AcquireAsync(request.DeviceId, destination.Value, cancellationToken);
        cached = await operations.GetAsync<CloseBagResponse>(identity.Value, OperationType, cancellationToken);
        if (cached is not null) return ReplayOrConflict(cached, request, identity, requestHash);

        IReadOnlyList<SelectedParcel> selected;
        try
        {
            selected = await selection.SelectAsync(new ParcelSelectionFilter(
                request.DeviceId,
                destination.Value,
                request.LastReadId,
                request.LastCanonicalBarcode,
                request.Count,
                request.From,
                request.To,
                request.ChuteIds,
                request.ParcelTypes,
                request.ServiceTypes), cancellationToken);
        }
        catch (DomainException ex)
        {
            return await StoreAsync(
                Error(request, identity, "INVALID_SELECTION", ex.Message), requestHash, cancellationToken);
        }

        if (selected.Count == 0)
        {
            return await StoreAsync(new CloseBagResponse(
                request.CorrelationId,
                identity.Value,
                BagCloseResult.NoEligibleParcel,
                null,
                null,
                new BagCounts(0, 0, 0, 0),
                [],
                "NO_ELIGIBLE_PARCEL",
                "No eligible parcels matched the bag closure filter.",
                identity.IsBestEffortFallback), requestHash, cancellationToken);
        }

        var selectedParcels = new List<EdgeParcel>(selected.Count);
        foreach (var item in selected)
        {
            var parcel = await parcels.FindByReadIdAsync(item.ReadId, cancellationToken);
            if (parcel is null || parcel.State is not (EdgeParcelState.DestinationAssigned or EdgeParcelState.Deferred))
            {
                return await StoreAsync(Error(
                    request, identity, "SELECTION_CHANGED", "The eligible set changed during closure."),
                    requestHash,
                    cancellationToken);
            }
            selectedParcels.Add(parcel);
        }

        var n = selectedParcels.Count(x => x.State == EdgeParcelState.DestinationAssigned);
        var p = selectedParcels.Count(x => x.State == EdgeParcelState.Deferred);
        var command = new PostalBagCommand(
            identity.Value,
            request.DeviceId,
            destination.Value,
            request.SealNumber,
            request.TransportType,
            selectedParcels.Select(x => new PostalBagParcel(
                x.ReadId,
                x.CanonicalBarcode,
                x.State == EdgeParcelState.Deferred,
                x.PhysicalAttributes.WeightGrams,
                x.ServiceType)).ToArray());

        var external = await externalOperations.GetAsync(identity.Value, OperationType, cancellationToken);
        PostalBagResult result;
        DateTimeOffset postalCalledAt;
        if (external is not null)
        {
            if (!string.Equals(external.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return await StoreAsync(Error(
                    request,
                    identity,
                    "IDEMPOTENCY_CONFLICT",
                    "The operation id was already used for a different bag request."), requestHash, cancellationToken);
            }

            result = ApplicationSupport.TryFromJson<PostalBagResult>(external.ReceiptJson)
                     ?? new PostalBagResult(
                         PostalBagStatus.Unavailable,
                         null,
                         [],
                         "POSTAL_OUTCOME_UNKNOWN",
                         "The durable Postal bag receipt is unavailable.");
            if (external.Finalized || result.Status == PostalBagStatus.Unavailable)
            {
                return await StoreAsync(Error(
                    request,
                    identity,
                    "MANUAL_RECONCILIATION_REQUIRED",
                    "A prior Postal bag call has no safely recoverable receipt and will not be repeated."),
                    requestHash,
                    cancellationToken);
            }
            postalCalledAt = external.CreatedAt;
        }
        else
        {
            externalOperations.AddIntent(new ExternalOperationSaga(
                identity.Value,
                OperationType,
                requestHash,
                ApplicationSupport.ToJson(command),
                null,
                false,
                clock.UtcNow,
                clock.UtcNow));
            await unitOfWork.SaveChangesAsync(cancellationToken);

            postalCalledAt = clock.UtcNow;
            try
            {
                result = await postal.CloseAsync(command, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new PostalBagResult(
                    PostalBagStatus.Unavailable, null, [], "POSTAL_UNAVAILABLE", "Postal bag closure timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                result = new PostalBagResult(
                    PostalBagStatus.Unavailable, null, [], "POSTAL_UNAVAILABLE", "Postal bag closure is unavailable.");
            }

            await externalOperations.StoreReceiptAsync(
                identity.Value,
                OperationType,
                ApplicationSupport.ToJson(result),
                clock.UtcNow,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        outbox.Enqueue(ApplicationSupport.OperationalResult(
            clock,
            "bag",
            identity.Value,
            selectedParcels.Count == 1 ? selectedParcels[0].CanonicalBarcode : null,
            $"PostalBag.{result.Status}",
            result.Status == PostalBagStatus.Completed,
            result.ErrorCode,
            result.ErrorMessage,
            postalCalledAt,
            1,
            result.Status switch
            {
                PostalBagStatus.Completed => "Succeeded",
                PostalBagStatus.Unavailable => "Pending",
                _ => "Failed"
            }));

        if (result.Status == PostalBagStatus.Unavailable)
        {
            return await StoreAsync(Error(
                request,
                identity,
                "MANUAL_RECONCILIATION_REQUIRED",
                "The Postal bag outcome is unknown and the non-idempotent operation will not be repeated."),
                requestHash,
                cancellationToken);
        }

        if (result.Status == PostalBagStatus.Failed)
        {
            return await StoreAsync(new CloseBagResponse(
                request.CorrelationId,
                identity.Value,
                BagCloseResult.Error,
                null,
                null,
                new BagCounts(n, p, n, p),
                [],
                result.ErrorCode ?? "POSTAL_REJECTED",
                result.ErrorMessage ?? "Postal bag closure failed.",
                identity.IsBestEffortFallback), requestHash, cancellationToken, true);
        }

        var selectedById = selectedParcels.ToDictionary(x => x.ReadId);
        if (result.Failures.Any(x => !selectedById.ContainsKey(x.ReadId)) ||
            result.Failures.Select(x => x.ReadId).Distinct().Count() != result.Failures.Count)
        {
            return await StoreAsync(Error(
                request, identity, "POSTAL_PROTOCOL_ERROR", "Postal service returned an invalid failure set."),
                requestHash,
                cancellationToken,
                true);
        }

        var failuresById = result.Failures.ToDictionary(x => x.ReadId);
        var m = selectedParcels.Count(x => x.State == EdgeParcelState.DestinationAssigned && failuresById.ContainsKey(x.ReadId));
        var q = selectedParcels.Count(x => x.State == EdgeParcelState.Deferred && failuresById.ContainsKey(x.ReadId));
        var counts = new BagCounts(n, p, m, q);
        if (counts.SuccessCount == 0)
        {
            ApplyFailures(selectedParcels, failuresById);
            return await StoreAsync(new CloseBagResponse(
                request.CorrelationId,
                identity.Value,
                BagCloseResult.AllParcelsFailed,
                null,
                null,
                counts,
                ToErrors(selectedById, result.Failures),
                "ALL_PARCELS_REJECTED",
                "Postal service rejected every selected parcel.",
                identity.IsBestEffortFallback), requestHash, cancellationToken, true);
        }

        if (string.IsNullOrWhiteSpace(result.BagBarcode))
        {
            return await StoreAsync(Error(
                request, identity, "POSTAL_PROTOCOL_ERROR", "Postal service omitted the final bag barcode."),
                requestHash,
                cancellationToken,
                true);
        }

        ApplyFailures(selectedParcels, failuresById);
        var successful = selectedParcels.Where(x => !failuresById.ContainsKey(x.ReadId)).ToArray();
        foreach (var parcel in successful)
        {
            parcel.MarkExportRegistered();
            parcel.MarkBagged(result.BagBarcode);
        }

        var label = labelGenerator.Generate(result.BagBarcode, request.DeviceId, destination.Value, counts, clock.UtcNow);
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new DomainException("The bag label generator returned an empty label.");
        }

        var domainErrors = result.Failures.Select(x => new ParcelError(x.ErrorCode, x.Description, x.Category ?? "Operational"));
        var bag = EdgeBag.Create(
            result.BagBarcode,
            identity.Value,
            request.DeviceId,
            destination,
            successful.Select(x => x.CanonicalBarcode),
            Encoding.UTF8.GetBytes(label),
            request.SealNumber,
            request.TransportType,
            counts,
            domainErrors,
            clock.UtcNow);
        bags.Add(bag);
        var configuration = await configurations.GetAsync(cancellationToken)
                            ?? throw new InvalidOperationException("The local Edge configuration is unavailable.");
        outbox.Enqueue(ApplicationSupport.Outbox(
            clock,
            "Core",
            ContractMessageTypes.EdgeBagClosed,
            new BagClosedV1(
                bag.LocalOperationId,
                bag.BagBarcode,
                bag.DeviceId,
                configuration.ExchangeCenterCode,
                bag.DestinationCenterCode,
                bag.MemberBarcodes,
                bag.SealNumber,
                bag.TransportType,
                bag.CreatedAt,
                new CoreBagClosureCountsV1(
                    bag.Counts.N,
                    bag.Counts.P,
                    bag.Counts.M,
                    bag.Counts.Q)),
            $"bag:{identity.Value:N}"));

        return await StoreAsync(new CloseBagResponse(
            request.CorrelationId,
            identity.Value,
            BagCloseResult.Completed,
            bag.BagBarcode,
            label,
            counts,
            ToErrors(selectedById, result.Failures),
            OperationIdIsBestEffort: identity.IsBestEffortFallback), requestHash, cancellationToken, true);
    }

    private static void ApplyFailures(
        IEnumerable<EdgeParcel> selectedParcels,
        Dictionary<Guid, PostalParcelFailure> failures)
    {
        foreach (var parcel in selectedParcels)
        {
            if (!failures.TryGetValue(parcel.ReadId, out var failure)) continue;
            var error = new ParcelError(failure.ErrorCode, failure.Description, failure.Category ?? "Operational");
            if (parcel.State == EdgeParcelState.Deferred) parcel.RecordDeferredFailure(error);
            else parcel.MarkDeferred(error);
        }
    }

    private static BagErrorDto[] ToErrors(
        Dictionary<Guid, EdgeParcel> selected,
        IEnumerable<PostalParcelFailure> failures) => failures
        .Select(x => new BagErrorDto(x.ReadId, selected[x.ReadId].CanonicalBarcode, x.ErrorCode, x.Description, x.Category))
        .ToArray();

    private static CloseBagResponse Error(
        CloseBagRequest request,
        OperationIdentity identity,
        string code,
        string message) => new(
        request.CorrelationId,
        identity.Value,
        BagCloseResult.Error,
        null,
        null,
        new BagCounts(0, 0, 0, 0),
        [],
        code,
        message,
        identity.IsBestEffortFallback);

    private async Task<CloseBagResponse> StoreAsync(
        CloseBagResponse response,
        string requestHash,
        CancellationToken cancellationToken,
        bool finalizeExternalOperation = false)
    {
        if (finalizeExternalOperation)
            await externalOperations.MarkFinalizedAsync(
                response.OperationId,
                OperationType,
                clock.UtcNow,
                cancellationToken);
        operations.AddResponse(response.OperationId, OperationType, response, clock.UtcNow, requestHash);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return response;
    }

    private static CloseBagResponse ReplayOrConflict(
        OperationJournalEntry<CloseBagResponse> cached,
        CloseBagRequest request,
        OperationIdentity identity,
        string requestHash) => string.Equals(cached.RequestHash, requestHash, StringComparison.Ordinal)
        ? cached.Response
        : Error(
            request,
            identity,
            "IDEMPOTENCY_CONFLICT",
            "The operation id was already used for a different bag request.");

    private static string Hash(CloseBagRequest request) => ApplicationSupport.RequestHash(new
    {
        request.DeviceId,
        DestinationCode = request.DestinationCode?.Trim(),
        request.LastReadId,
        LastCanonicalBarcode = request.LastCanonicalBarcode?.Trim(),
        request.Count,
        request.From,
        request.To,
        SealNumber = request.SealNumber?.Trim(),
        TransportType = request.TransportType?.Trim(),
        ChuteIds = request.ChuteIds?.Order(StringComparer.Ordinal).ToArray(),
        ParcelTypes = request.ParcelTypes?.Order(StringComparer.Ordinal).ToArray(),
        ServiceTypes = request.ServiceTypes?.Order(StringComparer.Ordinal).ToArray()
    });
}
