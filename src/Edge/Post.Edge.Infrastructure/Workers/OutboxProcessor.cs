using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Post.Contracts;
using Post.Contracts.V1.EdgeCore;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Persistence;

namespace Post.Edge.Infrastructure.Workers;

public sealed record OutboxProcessingResult(int Claimed, int Completed, int Retried, int DeadLettered);

public sealed class OutboxProcessor(
    EdgeDbContext db,
    ICoreApiClient core,
    ICoreImageUploadClient imageUploads,
    IInboundEvidenceRepository evidence,
    IEdgeParcelRepository parcels,
    IPendingInboundRepository pendingInbound,
    IOperationJournal operations,
    IOutboxWriter outbox,
    IDeviceEventPublisher devices,
    IClock clock,
    IOptions<EdgeWorkerOptions> options)
{
    public async Task<OutboxProcessingResult> ProcessBatchAsync(
        int? batchSize = null,
        CancellationToken cancellationToken = default)
    {
        var take = batchSize ?? options.Value.BatchSize;
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var now = clock.UtcNow;
        var rows = await db.Outbox
            .Where(x => x.ProcessedAt == null && x.NextAttemptAt <= now && (x.LockedUntil == null || x.LockedUntil < now))
            .OrderBy(x => x.OccurredAt)
            .Take(take)
            .ToListAsync(cancellationToken);
        foreach (var row in rows) row.LockedUntil = now.AddMinutes(1);
        await db.SaveChangesAsync(cancellationToken);

        var completed = 0;
        var retried = 0;
        var deadLettered = 0;
        foreach (var row in rows)
        {
            try
            {
                await DispatchAsync(row, cancellationToken);
                row.ProcessedAt = clock.UtcNow;
                row.LockedUntil = null;
                row.LastError = null;
                completed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                row.Attempts++;
                row.LastError = ClassifyFailure(ex);
                row.LockedUntil = null;
                if (row.Attempts >= options.Value.MaxOutboxAttempts)
                {
                    db.DeadLetters.Add(new DeadLetterRow
                    {
                        MessageId = row.MessageId,
                        Target = row.Target,
                        MessageType = row.MessageType,
                        PayloadJson = row.PayloadJson,
                        IdempotencyKey = row.IdempotencyKey,
                        OccurredAt = row.OccurredAt,
                        Attempts = row.Attempts,
                        FailedAt = clock.UtcNow,
                        Error = row.LastError
                    });
                    db.Outbox.Remove(row);
                    deadLettered++;
                }
                else
                {
                    row.NextAttemptAt = clock.UtcNow.AddSeconds(Math.Min(900, Math.Pow(2, row.Attempts)));
                    retried++;
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return new OutboxProcessingResult(rows.Count, completed, retried, deadLettered);
    }

    private async Task DispatchAsync(OutboxRow row, CancellationToken cancellationToken)
    {
        if (row.Target == "CoreLookup")
        {
            await CompleteDeferredCoreLookupAsync(row, cancellationToken);
            return;
        }
        if (row.Target == "Core")
        {
            await core.PublishAsync(row.MessageType, row.PayloadJson, row.IdempotencyKey, cancellationToken);
            await MarkCoreConfirmationAsync(row, cancellationToken);
            return;
        }
        if (row.Target == "Device")
        {
            await devices.PublishAsync(new DeviceEvent(row.MessageType, row.PayloadJson, row.IdempotencyKey), cancellationToken);
            return;
        }
        if (row.Target is "ImageUpload" or "SupplementaryUpload")
        {
            var metadata = PersistenceJson.Deserialize<ImageOutboxPayload>(row.PayloadJson);
            byte[] content;
            string? mimeType;
            string? description;
            string attachmentId;
            string attachmentType;
            if (row.Target == "ImageUpload")
            {
                var image = await evidence.FindImageAsync(
                    metadata.ReadId,
                    metadata.ImageId ?? string.Empty,
                    cancellationToken)
                    ?? throw new InvalidOperationException("Durable image content was not found.");
                content = image.Content;
                mimeType = image.MimeType;
                description = image.Description;
                attachmentId = image.ImageId;
                attachmentType = "parcel-image";
            }
            else
            {
                var supplementary = await evidence.FindSupplementaryDataAsync(metadata.ReadId, cancellationToken)
                                    ?? throw new InvalidOperationException("Durable supplementary content was not found.");
                content = System.Text.Encoding.UTF8.GetBytes(supplementary);
                mimeType = "application/json";
                description = null;
                attachmentId = "supplementary-data";
                attachmentType = "supplementary-data";
            }
            await imageUploads.UploadAsync(new CoreImageUpload(
                metadata.ReadId,
                metadata.ReadingRecordId,
                metadata.ParcelBarcode,
                attachmentId,
                content,
                mimeType,
                description,
                row.IdempotencyKey,
                metadata.CapturedAt,
                attachmentType), cancellationToken);
            return;
        }
        throw new InvalidOperationException($"Unsupported outbox target '{row.Target}'.");
    }

    private async Task MarkCoreConfirmationAsync(OutboxRow row, CancellationToken cancellationToken)
    {
        if (row.MessageType == ContractMessageTypes.EdgeBagClosed)
        {
            var payload = PersistenceJson.Deserialize<BagClosedV1>(row.PayloadJson);
            var bag = await db.Bags.SingleOrDefaultAsync(
                candidate => candidate.BagBarcode == payload.BagBarcode,
                cancellationToken);
            if (bag is not null) bag.CoreConfirmed = true;
            return;
        }
        if (row.MessageType == ContractMessageTypes.EdgeDispatchClosed)
        {
            var payload = PersistenceJson.Deserialize<DispatchClosedV1>(row.PayloadJson);
            var dispatch = await db.Dispatches.SingleOrDefaultAsync(
                candidate => candidate.LocalOperationId == payload.OperationId,
                cancellationToken);
            if (dispatch is not null) dispatch.CoreConfirmed = true;
            return;
        }
        if (row.MessageType is ContractMessageTypes.EdgeParcelReadingSubmitted or
            ContractMessageTypes.EdgeParcelStatusChanged)
        {
            var barcode = row.MessageType == ContractMessageTypes.EdgeParcelReadingSubmitted
                ? PersistenceJson.Deserialize<ParcelReadingSubmittedV1>(row.PayloadJson).CanonicalBarcode
                : PersistenceJson.Deserialize<ParcelStatusChangedV1>(row.PayloadJson).ParcelBarcode;
            var parcel = await db.Parcels
                .Where(candidate => candidate.CanonicalBarcode == barcode)
                .OrderByDescending(candidate => candidate.ReadTimestamp)
                .FirstOrDefaultAsync(cancellationToken);
            if (parcel is not null) parcel.CoreSynced = true;
        }
    }

    private async Task CompleteDeferredCoreLookupAsync(OutboxRow row, CancellationToken cancellationToken)
    {
        var deferred = PersistenceJson.Deserialize<DeferredCoreInboundLookup>(row.PayloadJson);
        var result = await core.LookupInboundAsync(deferred.Request, cancellationToken);
        var parcel = await parcels.FindByReadIdAsync(deferred.ReadId, cancellationToken)
                     ?? throw new InvalidOperationException("Deferred Core lookup parcel was not found.");
        var postalPending = await pendingInbound.FindByOperationIdAsync(
            deferred.Request.OperationId,
            cancellationToken);
        var cached = await operations.GetResponseAsync<InboundParcelResponse>(
            deferred.Request.OperationId,
            "parcel.inbound",
            cancellationToken);

        if (result.Disposition is CoreParcelDisposition.Returned or CoreParcelDisposition.Refused)
        {
            parcel.RejectInboundDecision(new ParcelError(
                result.Disposition == CoreParcelDisposition.Returned ? "CORE_RETURNED" : "CORE_REFUSED",
                "Core rejected the deferred inbound decision."));
            if (postalPending is not null) pendingInbound.Remove(postalPending);
            if (cached is not null)
            {
                operations.AddResponse(deferred.Request.OperationId, "parcel.inbound", cached with
                {
                    Status = result.Disposition == CoreParcelDisposition.Returned
                        ? InboundResultStatus.Returned
                        : InboundResultStatus.Refused,
                    DiscrepancyCode = result.DiscrepancyCode,
                    ErrorCode = null,
                    ErrorMessage = null
                }, clock.UtcNow);
            }
            return;
        }

        if (parcel.State == EdgeParcelState.Pending && postalPending is null) parcel.ResolvePending();
        EnqueueDeferredAttachments(deferred, result.ReadingRecordId ?? deferred.ReadId);
        if (postalPending is null)
        {
            EnqueueParcelReading(deferred, parcel);
            if (cached is not null)
            {
                operations.AddResponse(deferred.Request.OperationId, "parcel.inbound", cached with
                {
                    Status = InboundResultStatus.Registered,
                    DiscrepancyCode = result.DiscrepancyCode,
                    ErrorCode = null,
                    ErrorMessage = null
                }, clock.UtcNow);
            }
        }
    }

    private void EnqueueDeferredAttachments(
        DeferredCoreInboundLookup deferred,
        Guid readingRecordId)
    {
        if (deferred.HasSupplementaryData)
        {
            outbox.Enqueue(NewOutbox(
                "SupplementaryUpload",
                "EdgeParcelSupplementaryDataUploadRequested",
                new
                {
                    deferred.ReadId,
                    ReadingRecordId = readingRecordId,
                    deferred.ParcelBarcode,
                    deferred.CapturedAt
                },
                $"supplementary:{deferred.Request.OperationId:N}"));
        }
        foreach (var imageId in deferred.ImageIds)
        {
            outbox.Enqueue(NewOutbox(
                "ImageUpload",
                "EdgeParcelImageUploadRequested",
                new
                {
                    deferred.ReadId,
                    ReadingRecordId = readingRecordId,
                    deferred.ParcelBarcode,
                    ImageId = imageId,
                    deferred.CapturedAt
                },
                $"image:{deferred.Request.OperationId:N}:{imageId}"));
        }
    }

    private void EnqueueParcelReading(DeferredCoreInboundLookup deferred, EdgeParcel parcel) =>
        outbox.Enqueue(NewOutbox(
            "Core",
            ContractMessageTypes.EdgeParcelReadingSubmitted,
            new ParcelReadingSubmittedV1(
                deferred.Request.OperationId,
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
                deferred.OriginCenterCode,
                deferred.DestinationCenterCode),
            $"inbound:{deferred.Request.OperationId:N}"));

    private OutboxMessage NewOutbox(string target, string messageType, object payload, string idempotencyKey) => new(
        Guid.NewGuid(),
        target,
        messageType,
        PersistenceJson.Serialize(payload),
        idempotencyKey,
        clock.UtcNow,
        clock.UtcNow);

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        HttpRequestException => "HTTP_DEPENDENCY_FAILURE",
        TimeoutException or TaskCanceledException => "DEPENDENCY_TIMEOUT",
        System.Text.Json.JsonException => "INVALID_DEPENDENCY_PAYLOAD",
        _ => "OUTBOX_PROCESSING_FAILURE"
    };

    private sealed record ImageOutboxPayload(
        Guid ReadId,
        Guid ReadingRecordId,
        string ParcelBarcode,
        string? ImageId,
        DateTimeOffset CapturedAt);
}

public sealed class DeadLetterService(EdgeDbContext db, IClock clock)
{
    public async Task<int> CountAsync(CancellationToken cancellationToken = default) =>
        await db.DeadLetters.CountAsync(cancellationToken);

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        var row = await db.DeadLetters.SingleOrDefaultAsync(x => x.MessageId == messageId, cancellationToken);
        if (row is null) return false;
        db.Outbox.Add(new OutboxRow
        {
            MessageId = row.MessageId,
            Target = row.Target,
            MessageType = row.MessageType,
            PayloadJson = row.PayloadJson,
            IdempotencyKey = row.IdempotencyKey,
            OccurredAt = row.OccurredAt,
            NextAttemptAt = clock.UtcNow
        });
        db.DeadLetters.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
