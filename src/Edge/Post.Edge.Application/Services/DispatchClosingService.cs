using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Dispatches;
using Post.Edge.Domain.ValueObjects;
using Post.Contracts;
using Post.Contracts.V1.EdgeCore;

namespace Post.Edge.Application.Services;

public sealed class DispatchClosingService(
    IEdgeBagRepository bags,
    IDispatchRepository dispatches,
    IPostalDispatchClient postal,
    IOperationJournal operations,
    IExternalOperationSagaRepository externalOperations,
    IOutboxWriter outbox,
    IEdgeUnitOfWork unitOfWork,
    IDestinationLockProvider locks,
    IClock clock) : IDispatchClosingService
{
    private const string OperationType = "dispatch.close";

    public async Task<CloseDispatchResponse> CloseAsync(
        CloseDispatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = OperationIdentity.Resolve(request.OperationId, request.CorrelationId);
        var requestHash = Hash(request);
        var cached = await operations.GetAsync<CloseDispatchResponse>(identity.Value, OperationType, cancellationToken);
        if (cached is not null) return ReplayOrConflict(cached, request, identity, requestHash);

        string destination;
        string[]? requestedBags = null;
        try
        {
            destination = PostalCenterCode.Create(request.DestinationCode).Value;
            if (request.BagBarcodes is { Count: > 0 })
            {
                requestedBags = request.BagBarcodes
                    .Select(x => x?.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (requestedBags.Length != request.BagBarcodes.Count)
                    throw new ArgumentException("Explicit bag barcodes must be unique and non-empty.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or Domain.Common.DomainException)
        {
            return await StoreAsync(
                Error(request, identity, "INVALID_DISPATCH", ex.Message), requestHash, cancellationToken);
        }

        await using var destinationLock = await locks.AcquireAsync(request.DeviceId, destination, cancellationToken);
        cached = await operations.GetAsync<CloseDispatchResponse>(identity.Value, OperationType, cancellationToken);
        if (cached is not null) return ReplayOrConflict(cached, request, identity, requestHash);

        var found = requestedBags is null
            ? await bags.ListUndispatchedAsync(request.DeviceId, destination, cancellationToken)
            : await bags.FindByBarcodesAsync(requestedBags, cancellationToken);
        requestedBags ??= found.Select(x => x.BagBarcode).ToArray();
        if (requestedBags.Length == 0)
        {
            return await StoreAsync(Error(
                request,
                identity,
                "NO_UNDISPATCHED_BAG",
                "No undispatched bag exists for this device and destination."), requestHash, cancellationToken);
        }
        if (found.Count != requestedBags.Length || found.Any(x =>
                !string.Equals(x.DeviceId, request.DeviceId, StringComparison.Ordinal) ||
                !string.Equals(x.DestinationCenterCode, destination, StringComparison.Ordinal) ||
                x.DispatchAssigned))
        {
            return await StoreAsync(Error(
                request,
                identity,
                "BAG_NOT_ELIGIBLE",
                "Every bag must exist, belong to this device and destination, and be undispatched."),
                requestHash,
                cancellationToken);
        }

        var external = await externalOperations.GetAsync(identity.Value, OperationType, cancellationToken);
        PostalDispatchResult postalResult;
        DateTimeOffset postalCalledAt;
        if (external is not null)
        {
            if (!string.Equals(external.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return await StoreAsync(Error(
                    request,
                    identity,
                    "IDEMPOTENCY_CONFLICT",
                    "The operation id was already used for a different dispatch request."), requestHash, cancellationToken);
            }

            postalResult = ApplicationSupport.TryFromJson<PostalDispatchResult>(external.ReceiptJson)
                           ?? new PostalDispatchResult(
                               false,
                               null,
                               "POSTAL_OUTCOME_UNKNOWN",
                               "The durable Postal dispatch receipt is unavailable.");
            if (external.Finalized ||
                string.Equals(postalResult.ErrorCode, "POSTAL_UNAVAILABLE", StringComparison.Ordinal) ||
                string.Equals(postalResult.ErrorCode, "POSTAL_OUTCOME_UNKNOWN", StringComparison.Ordinal))
            {
                return await StoreAsync(Error(
                    request,
                    identity,
                    "MANUAL_RECONCILIATION_REQUIRED",
                    "A prior Postal dispatch call has no safely recoverable receipt and will not be repeated."),
                    requestHash,
                    cancellationToken);
            }
            postalCalledAt = external.CreatedAt;
        }
        else
        {
            var command = new PostalDispatchCommand(
                identity.Value,
                request.DeviceId,
                destination,
                requestedBags);
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
                postalResult = await postal.CloseAsync(command, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The authoritative device protocol requires an immediate error. The device retries.
                postalResult = new PostalDispatchResult(
                    false, null, "POSTAL_UNAVAILABLE", "Postal dispatch closure timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                postalResult = new PostalDispatchResult(
                    false, null, "POSTAL_UNAVAILABLE", "Postal dispatch closure is unavailable.");
            }

            await externalOperations.StoreReceiptAsync(
                identity.Value,
                OperationType,
                ApplicationSupport.ToJson(postalResult),
                clock.UtcNow,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        outbox.Enqueue(ApplicationSupport.OperationalResult(
            clock,
            "dispatch",
            identity.Value,
            null,
            postalResult.IsSuccess ? "PostalDispatch.Completed" : "PostalDispatch.Failed",
            postalResult.IsSuccess,
            postalResult.ErrorCode,
            postalResult.ErrorMessage,
            postalCalledAt,
            1,
            postalResult.IsSuccess ? "Succeeded" : "Failed"));

        if (!postalResult.IsSuccess || string.IsNullOrWhiteSpace(postalResult.DispatchId))
        {
            if (string.Equals(postalResult.ErrorCode, "POSTAL_UNAVAILABLE", StringComparison.Ordinal))
            {
                return await StoreAsync(Error(
                    request,
                    identity,
                    "MANUAL_RECONCILIATION_REQUIRED",
                    "The Postal dispatch outcome is unknown and the non-idempotent operation will not be repeated."),
                    requestHash,
                    cancellationToken);
            }
            return await StoreAsync(Error(
                request,
                identity,
                postalResult.ErrorCode ?? "POSTAL_DISPATCH_FAILED",
                postalResult.ErrorMessage ?? "Postal dispatch closure failed."), requestHash, cancellationToken, true);
        }

        foreach (var bag in found) bag.AssignDispatch(postalResult.DispatchId);
        var totalParcels = found.Sum(bag => bag.MemberBarcodes.Count);
        var dispatch = DispatchRecord.Create(
            identity.Value,
            postalResult.DispatchId,
            request.DeviceId,
            destination,
            requestedBags,
            clock.UtcNow);
        dispatches.Add(dispatch);
        outbox.Enqueue(ApplicationSupport.Outbox(
            clock,
            "Core",
            ContractMessageTypes.EdgeDispatchClosed,
            new DispatchClosedV1(
                dispatch.LocalOperationId,
                dispatch.DispatchId,
                dispatch.DestinationCenterCode,
                dispatch.BagBarcodes,
                dispatch.CreatedAt,
                false,
                [new CoreDispatchDeviceStatisticsV1(
                    dispatch.DeviceId,
                    dispatch.BagBarcodes.Count,
                    totalParcels,
                    0)]),
            $"dispatch:{identity.Value:N}"));

        return await StoreAsync(new CloseDispatchResponse(
            request.CorrelationId,
            identity.Value,
            1,
            dispatch.DispatchId,
            dispatch.BagBarcodes,
            totalParcels,
            [],
            OperationIdIsBestEffort: identity.IsBestEffortFallback), requestHash, cancellationToken, true);
    }

    private static CloseDispatchResponse Error(
        CloseDispatchRequest request,
        OperationIdentity identity,
        string code,
        string message) => new(
        request.CorrelationId,
        identity.Value,
        2,
        null,
        request.BagBarcodes ?? [],
        0,
        [],
        code,
        message,
        identity.IsBestEffortFallback);

    private async Task<CloseDispatchResponse> StoreAsync(
        CloseDispatchResponse response,
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

    private static CloseDispatchResponse ReplayOrConflict(
        OperationJournalEntry<CloseDispatchResponse> cached,
        CloseDispatchRequest request,
        OperationIdentity identity,
        string requestHash) => string.Equals(cached.RequestHash, requestHash, StringComparison.Ordinal)
        ? cached.Response
        : Error(
            request,
            identity,
            "IDEMPOTENCY_CONFLICT",
            "The operation id was already used for a different dispatch request.");

    private static string Hash(CloseDispatchRequest request) => ApplicationSupport.RequestHash(new
    {
        request.DeviceId,
        DestinationCode = request.DestinationCode?.Trim(),
        BagBarcodes = request.BagBarcodes?.Order(StringComparer.Ordinal).ToArray()
    });
}
