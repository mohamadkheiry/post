using System.Text;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Bags;
using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;
using Post.Contracts;
using Post.Contracts.V1.EdgeCore;

namespace Post.Edge.Application.Services;

public sealed class PendingWorkService(
    IPendingInboundRepository pendingInbound,
    IPendingBagClosureRepository pendingBags,
    IEdgeParcelRepository parcels,
    IEdgeBagRepository bags,
    IDeviceConfigurationRepository configurations,
    IPostalInboundClient inboundClient,
    IPostalBagClient bagClient,
    IBagLabelGenerator labelGenerator,
    IOperationJournal operations,
    IOutboxWriter outbox,
    IOutboxStatus outboxStatus,
    IEdgeUnitOfWork unitOfWork,
    IClock clock) : IPendingWorkService
{
    private const int MaxAttempts = 5;

    public async Task<PendingWorkResult> ProcessInboundAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var due = await pendingInbound.ListDueAsync(clock.UtcNow, batchSize, cancellationToken);
        var completed = 0;
        var retried = 0;
        var deadLettered = 0;

        foreach (var item in due)
        {
            var cached = await operations.GetResponseAsync<InboundParcelResponse>(
                item.OperationId, "parcel.inbound", cancellationToken);
            var parcel = await parcels.FindByReadIdAsync(item.ReadId, cancellationToken);
            if (parcel is null || parcel.State != EdgeParcelState.Pending)
            {
                pendingInbound.Remove(item);
                deadLettered++;
                if (cached is not null)
                {
                    operations.AddResponse(item.OperationId, "parcel.inbound", cached with
                    {
                        Status = InboundResultStatus.Error,
                        ErrorCode = "PENDING_INBOUND_ORPHANED",
                        ErrorMessage = "The pending inbound parcel is no longer available."
                    }, clock.UtcNow);
                }
                outbox.Enqueue(ApplicationSupport.Outbox(
                    clock,
                    "DeadLetter",
                    "PendingInboundOrphaned",
                    item,
                    $"pending-inbound-orphan:{item.OperationId:N}"));
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            PostalInboundResult result;
            try
            {
                result = await inboundClient.GetPendingResultAsync(item.PostalTrackingId, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new PostalInboundResult(
                    PostalInboundStatus.Pending,
                    item.PostalTrackingId,
                    "POSTAL_UNAVAILABLE",
                    "Postal pending-inbound lookup timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                result = new PostalInboundResult(
                    PostalInboundStatus.Pending,
                    item.PostalTrackingId,
                    "POSTAL_UNAVAILABLE",
                    "Postal pending-inbound lookup is unavailable.");
            }

            if (result.Status == PostalInboundStatus.Registered)
            {
                parcel.ResolvePending();
                pendingInbound.Remove(item);
                completed++;
                if (!await outboxStatus.IsPendingAsync($"core-lookup:{item.OperationId:N}", cancellationToken))
                {
                    var configuration = await configurations.GetAsync(cancellationToken)
                                        ?? throw new InvalidOperationException(
                                            "The local Edge configuration is unavailable.");
                    var destinationCode = cached?.DestinationCode ??
                                          (parcel.CanonicalBarcode.Length == 24
                                              ? parcel.CanonicalBarcode.Substring(14, 5)
                                              : throw new InvalidOperationException(
                                                  "The pending inbound destination is unavailable."));
                    outbox.Enqueue(ApplicationSupport.Outbox(
                        clock,
                        "Core",
                        ContractMessageTypes.EdgeParcelReadingSubmitted,
                        new ParcelReadingSubmittedV1(
                            item.OperationId,
                            parcel.CanonicalBarcode,
                            parcel.Aliases,
                            parcel.DeviceId,
                            parcel.ReadTimestamp,
                            new CorePhysicalAttributesV1(
                                parcel.PhysicalAttributes.WeightGrams,
                                parcel.PhysicalAttributes.Dimensions?.LengthMm,
                                parcel.PhysicalAttributes.Dimensions?.WidthMm,
                                parcel.PhysicalAttributes.Dimensions?.HeightMm),
                            CoreOperationStatusV1.Succeeded,
                            null,
                            configuration.ExchangeCenterCode,
                            destinationCode),
                        $"inbound:{item.OperationId:N}"));
                }
                if (cached is not null)
                {
                    operations.AddResponse(item.OperationId, "parcel.inbound", cached with
                    {
                        Status = InboundResultStatus.Registered,
                        ErrorCode = null,
                        ErrorMessage = null
                    }, clock.UtcNow);
                }
            }
            else if (result.Status == PostalInboundStatus.Failed || item.Attempts + 1 >= MaxAttempts)
            {
                var error = new ParcelError(
                    result.ErrorCode ?? "PENDING_INBOUND_FAILED",
                    result.ErrorMessage ?? "Pending postal inbound registration exhausted its retry policy.");
                parcel.FailPending(error);
                pendingInbound.Remove(item);
                deadLettered++;
                if (cached is not null)
                {
                    operations.AddResponse(item.OperationId, "parcel.inbound", cached with
                    {
                        Status = InboundResultStatus.Error,
                        ErrorCode = error.Code,
                        ErrorMessage = error.Description
                    }, clock.UtcNow);
                }
                outbox.Enqueue(ApplicationSupport.Outbox(
                    clock,
                    "DeadLetter",
                    "PendingInboundFailed",
                    new { item.OperationId, item.ReadId, error.Code, error.Description },
                    $"pending-inbound-failed:{item.OperationId:N}"));
            }
            else
            {
                var attempts = item.Attempts + 1;
                pendingInbound.Update(item with
                {
                    Attempts = attempts,
                    NextAttemptAt = NextAttempt(attempts),
                    LastError = result.ErrorMessage
                });
                retried++;
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new PendingWorkResult(due.Count, completed, retried, deadLettered);
    }

    public async Task<PendingWorkResult> ProcessBagClosuresAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var due = await pendingBags.ListDueAsync(clock.UtcNow, batchSize, cancellationToken);
        var completed = 0;
        var retried = 0;
        var deadLettered = 0;

        foreach (var closure in due)
        {
            var cached = await operations.GetResponseAsync<CloseBagResponse>(
                closure.LocalOperationId, "bag.close", cancellationToken);
            var selected = new List<EdgeParcel>(closure.ParcelReadIds.Count);
            foreach (var readId in closure.ParcelReadIds)
            {
                var parcel = await parcels.FindByReadIdAsync(readId, cancellationToken);
                if (parcel is not null) selected.Add(parcel);
            }

            if (selected.Count != closure.ParcelReadIds.Count ||
                selected.Any(x => x.State is not (EdgeParcelState.DestinationAssigned or EdgeParcelState.Deferred)))
            {
                closure.MarkPermanentlyFailed("The original eligible parcel set is no longer available.");
                deadLettered++;
                UpdateBagJournal(
                    cached,
                    closure.LocalOperationId,
                    "SELECTION_CHANGED",
                    "The original eligible parcel set is no longer available.");
                EnqueueBagDeadLetter(closure);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            var n = selected.Count(x => x.State == EdgeParcelState.DestinationAssigned);
            var p = selected.Count - n;
            PostalBagResult result;
            try
            {
                result = await bagClient.CloseAsync(new PostalBagCommand(
                    closure.LocalOperationId,
                    closure.DeviceId,
                    closure.DestinationCenterCode,
                    closure.SealNumber,
                    closure.TransportType,
                    selected.Select(x => new PostalBagParcel(
                        x.ReadId,
                        x.CanonicalBarcode,
                        x.State == EdgeParcelState.Deferred,
                        x.PhysicalAttributes.WeightGrams,
                        x.ServiceType)).ToArray()), cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new PostalBagResult(
                    PostalBagStatus.Unavailable,
                    null,
                    [],
                    "POSTAL_UNAVAILABLE",
                    "Postal pending-bag closure timed out.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                result = new PostalBagResult(
                    PostalBagStatus.Unavailable,
                    null,
                    [],
                    "POSTAL_UNAVAILABLE",
                    "Postal pending-bag closure is unavailable.");
            }

            if (result.Status == PostalBagStatus.Unavailable)
            {
                if (closure.Attempts + 1 >= MaxAttempts)
                {
                    closure.MarkPermanentlyFailed(result.ErrorMessage ?? "Postal bag closure exhausted its retry policy.");
                    deadLettered++;
                    UpdateBagJournal(
                        cached,
                        closure.LocalOperationId,
                        result.ErrorCode ?? "POSTAL_UNAVAILABLE",
                        "Postal bag closure exhausted its retry policy.");
                    EnqueueBagDeadLetter(closure);
                }
                else
                {
                    closure.RecordFailedAttempt(
                        result.ErrorMessage ?? "Postal service is unavailable.",
                        NextAttempt(closure.Attempts + 1));
                    retried++;
                }
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (result.Status != PostalBagStatus.Completed)
            {
                closure.MarkPermanentlyFailed(result.ErrorMessage ?? "Postal service rejected pending bag closure.");
                deadLettered++;
                UpdateBagJournal(
                    cached,
                    closure.LocalOperationId,
                    result.ErrorCode ?? "POSTAL_REJECTED",
                    result.ErrorMessage ?? "Postal service rejected pending bag closure.");
                EnqueueBagDeadLetter(closure);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            var selectedById = selected.ToDictionary(x => x.ReadId);
            if (result.Failures.Any(x => !selectedById.ContainsKey(x.ReadId)) ||
                result.Failures.Select(x => x.ReadId).Distinct().Count() != result.Failures.Count)
            {
                closure.MarkPermanentlyFailed("Postal service returned an invalid failure set.");
                deadLettered++;
                UpdateBagJournal(
                    cached,
                    closure.LocalOperationId,
                    "POSTAL_PROTOCOL_ERROR",
                    "Postal service returned an invalid failure set.");
                EnqueueBagDeadLetter(closure);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            var failures = result.Failures.ToDictionary(x => x.ReadId);
            var m = selected.Count(x => x.State == EdgeParcelState.DestinationAssigned && failures.ContainsKey(x.ReadId));
            var q = selected.Count(x => x.State == EdgeParcelState.Deferred && failures.ContainsKey(x.ReadId));
            var counts = new BagCounts(n, p, m, q);
            if (counts.SuccessCount == 0)
            {
                foreach (var parcel in selected)
                {
                    var failure = failures[parcel.ReadId];
                    var error = new ParcelError(
                        failure.ErrorCode,
                        failure.Description,
                        failure.Category ?? "Operational");
                    if (parcel.State == EdgeParcelState.Deferred) parcel.RecordDeferredFailure(error);
                    else parcel.MarkDeferred(error);
                }

                closure.MarkPermanentlyFailed("Postal service rejected every pending parcel.");
                deadLettered++;
                EnqueueBagDeadLetter(closure);
                if (cached is not null)
                {
                    operations.AddResponse(closure.LocalOperationId, "bag.close", cached with
                    {
                        Result = BagCloseResult.AllParcelsFailed,
                        Counts = counts,
                        Errors = result.Failures.Select(failure => new BagErrorDto(
                            failure.ReadId,
                            selectedById[failure.ReadId].CanonicalBarcode,
                            failure.ErrorCode,
                            failure.Description,
                            failure.Category)).ToArray(),
                        ErrorCode = "ALL_PARCELS_REJECTED",
                        ErrorMessage = "Postal service rejected every selected parcel."
                    }, clock.UtcNow);
                }
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (string.IsNullOrWhiteSpace(result.BagBarcode))
            {
                closure.MarkPermanentlyFailed("Postal service omitted the final bag barcode.");
                deadLettered++;
                UpdateBagJournal(
                    cached,
                    closure.LocalOperationId,
                    "POSTAL_PROTOCOL_ERROR",
                    "Postal service omitted the final bag barcode.");
                EnqueueBagDeadLetter(closure);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            foreach (var parcel in selected)
            {
                if (failures.TryGetValue(parcel.ReadId, out var failure))
                {
                    var error = new ParcelError(failure.ErrorCode, failure.Description, failure.Category ?? "Operational");
                    if (parcel.State == EdgeParcelState.Deferred) parcel.RecordDeferredFailure(error);
                    else parcel.MarkDeferred(error);
                }
                else
                {
                    parcel.MarkExportRegistered();
                    parcel.MarkBagged(result.BagBarcode);
                }
            }

            var label = labelGenerator.Generate(
                result.BagBarcode,
                closure.DeviceId,
                closure.DestinationCenterCode,
                counts,
                clock.UtcNow);
            var bag = EdgeBag.Create(
                result.BagBarcode,
                closure.LocalOperationId,
                closure.DeviceId,
                PostalCenterCode.Create(closure.DestinationCenterCode),
                selected.Where(x => !failures.ContainsKey(x.ReadId)).Select(x => x.CanonicalBarcode),
                Encoding.UTF8.GetBytes(label),
                closure.SealNumber,
                closure.TransportType,
                counts,
                result.Failures.Select(x => new ParcelError(x.ErrorCode, x.Description, x.Category ?? "Operational")),
                clock.UtcNow);
            bags.Add(bag);
            closure.Complete(bag.BagBarcode);
            completed++;
            outbox.Enqueue(ApplicationSupport.Outbox(
                clock,
                "Core",
                ContractMessageTypes.EdgeBagClosed,
                ToCoreBag(
                    bag,
                    (await configurations.GetAsync(cancellationToken)
                     ?? throw new InvalidOperationException("The local Edge configuration is unavailable."))
                    .ExchangeCenterCode),
                $"bag:{closure.LocalOperationId:N}"));
            outbox.Enqueue(ApplicationSupport.Outbox(
                clock,
                "Device",
                "EdgeBagLabelReady",
                new { OperationId = closure.LocalOperationId, bag.BagBarcode, LabelPayload = label, bag.Counts },
                $"bag-label:{closure.LocalOperationId:N}"));
            if (cached is not null)
            {
                operations.AddResponse(closure.LocalOperationId, "bag.close", cached with
                {
                    Result = BagCloseResult.Completed,
                    BagBarcode = bag.BagBarcode,
                    LabelPayload = label,
                    Counts = counts,
                    Errors = result.Failures.Select(x => new BagErrorDto(
                        x.ReadId,
                        selectedById[x.ReadId].CanonicalBarcode,
                        x.ErrorCode,
                        x.Description,
                        x.Category)).ToArray(),
                    ErrorCode = null,
                    ErrorMessage = null
                }, clock.UtcNow);
            }
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return new PendingWorkResult(due.Count, completed, retried, deadLettered);
    }

    private DateTimeOffset NextAttempt(int attempt) =>
        clock.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, attempt)));

    private void UpdateBagJournal(
        CloseBagResponse? cached,
        Guid operationId,
        string errorCode,
        string errorMessage)
    {
        if (cached is null) return;
        operations.AddResponse(operationId, "bag.close", cached with
        {
            Result = BagCloseResult.Error,
            BagBarcode = null,
            LabelPayload = null,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        }, clock.UtcNow);
    }

    private void EnqueueBagDeadLetter(PendingBagClosure closure) => outbox.Enqueue(ApplicationSupport.Outbox(
        clock,
        "DeadLetter",
        "PendingBagClosureFailed",
        new { closure.LocalOperationId, closure.DeviceId, closure.DestinationCenterCode, closure.LastError },
        $"pending-bag-failed:{closure.LocalOperationId:N}"));

    private static BagClosedV1 ToCoreBag(EdgeBag bag, string originCenterCode) => new(
        bag.LocalOperationId,
        bag.BagBarcode,
        bag.DeviceId,
        originCenterCode,
        bag.DestinationCenterCode,
        bag.MemberBarcodes,
        bag.SealNumber,
        bag.TransportType,
        bag.CreatedAt,
        new CoreBagClosureCountsV1(bag.Counts.N, bag.Counts.P, bag.Counts.M, bag.Counts.Q));
}
