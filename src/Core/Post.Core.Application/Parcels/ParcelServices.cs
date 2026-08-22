using System.Text.Json;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Application.Parcels;

public sealed class InboundValidationService : IInboundValidationService
{
    private readonly IDurableEventPublisher _eventPublisher;
    private readonly ICorrelationService _correlationService;

    public InboundValidationService(
        IDurableEventPublisher eventPublisher,
        ICorrelationService correlationService)
    {
        _eventPublisher = eventPublisher;
        _correlationService = correlationService;
    }

    public async Task<DurablePublishReceipt> ValidateAndPublishAsync(
        IngestionEventCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.EventId == Guid.Empty || string.IsNullOrWhiteSpace(command.Topic))
        {
            throw new CoreValidationException("A non-empty event identifier and topic are required.");
        }

        var idempotencyKey = new IdempotencyKey(command.IdempotencyKey);
        var correlationId = _correlationService.GetOrCreate(command.CorrelationId);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(command.Payload);
        if (payloadBytes.AsSpan().SequenceEqual("null"u8))
        {
            throw new CoreValidationException("Event payload is required.");
        }

        if (payloadBytes.Length > IngestionPayloadLimits.MaximumUtf8Bytes)
        {
            throw new CorePayloadTooLargeException(
                $"Event payload must not exceed {IngestionPayloadLimits.MaximumUtf8Bytes} UTF-8 bytes.");
        }

        var payload = System.Text.Encoding.UTF8.GetString(payloadBytes);

        try
        {
            // Success is returned only after the configured publisher confirms durable acceptance.
            return await _eventPublisher.PublishAsync(
                new EventEnvelope(
                    command.EventId,
                    command.Topic.Trim(),
                    idempotencyKey.Value,
                    payload,
                    correlationId,
                    command.OccurredAt,
                    BusinessFingerprint: command.BusinessFingerprint),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not CoreApplicationException and not OperationCanceledException)
        {
            throw new CoreDependencyUnavailableException("The event could not be durably published.", exception);
        }
    }
}

public sealed class InboundQueryService : IInboundQueryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IParcelLifecycleStore _store;
    private readonly BarcodeValidator _barcodeValidator;
    private readonly IDuplicateReadingClassifier _classifier;
    private readonly IDiscrepancyChecker _discrepancyChecker;
    private readonly ICoreDeviceStatusMapper _statusMapper;
    private readonly IRequestHasher _requestHasher;
    private readonly IIdempotencyService _idempotencyService;
    private readonly TimeProvider _timeProvider;
    private readonly OperationalSettings _settings;

    public InboundQueryService(
        IParcelLifecycleStore store,
        BarcodeValidator barcodeValidator,
        IDuplicateReadingClassifier classifier,
        IDiscrepancyChecker discrepancyChecker,
        ICoreDeviceStatusMapper statusMapper,
        IRequestHasher requestHasher,
        IIdempotencyService idempotencyService,
        TimeProvider timeProvider,
        OperationalSettings settings)
    {
        _store = store;
        _barcodeValidator = barcodeValidator;
        _classifier = classifier;
        _discrepancyChecker = discrepancyChecker;
        _statusMapper = statusMapper;
        _requestHasher = requestHasher;
        _idempotencyService = idempotencyService;
        _timeProvider = timeProvider;
        _settings = settings;
    }

    public async Task<InboundQueryResult> ExecuteAsync(
        InboundQueryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.PhysicalAttributes);

        var validatedBarcodes = _barcodeValidator.Validate(command.Barcodes);
        var key = new IdempotencyKey(command.IdempotencyKey);
        var requestHash = _requestHasher.Hash(new
        {
            IdempotencyKey = key.Value,
            Barcodes = validatedBarcodes.Barcodes.Select(x => x.Value).Order(StringComparer.Ordinal).ToArray(),
            command.ParcelType,
            command.ServiceType,
            command.OriginCenterCode,
            command.DestinationCenterCode,
            command.DeviceId,
            command.ExchangeCenterCode,
            command.PhysicalAttributes,
            command.EdgeEventTime
        });

        var existingIdempotency = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
        var decision = _idempotencyService.Evaluate(existingIdempotency, requestHash);
        if (decision == IdempotencyDecision.Replay)
        {
            var replay = JsonSerializer.Deserialize<InboundQueryResult>(
                existingIdempotency!.ResponseSnapshot,
                JsonOptions) ?? throw new CoreConflictException("Stored idempotent response is invalid.");
            return replay with { IsReplay = true };
        }

        if (decision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("The same idempotent request is still in progress.");
        }

        var center = new ExchangeCenterCode(command.ExchangeCenterCode);
        var physicalAttributes = new PhysicalAttributes(
            command.PhysicalAttributes.WeightKg,
            command.PhysicalAttributes.LengthCm,
            command.PhysicalAttributes.WidthCm,
            command.PhysicalAttributes.HeightCm);
        var dossier = await _store.FindParcelAsync(validatedBarcodes.PrimaryBarcode, cancellationToken)
            .ConfigureAwait(false);
        var isNewDossier = dossier is null;
        dossier ??= ParcelDossier.Create(
            validatedBarcodes.PrimaryBarcode,
            command.ParcelType,
            command.ServiceType,
            new ExchangeCenterCode(command.OriginCenterCode),
            new ExchangeCenterCode(command.DestinationCenterCode));

        var previousReading = dossier.LatestReading(center);
        var classification = _classifier.Classify(
            command.EdgeEventTime,
            previousReading?.Timestamp,
            _settings);
        var coreStatus = classification switch
        {
            ReadingClassification.Success => CoreParcelStatus.Success,
            ReadingClassification.Returned => CoreParcelStatus.Returned,
            ReadingClassification.Refused => CoreParcelStatus.Refused,
            _ => throw new InvalidOperationException($"Unknown reading classification '{classification}'.")
        };

        var discrepancies = previousReading is null
            ? Array.Empty<PhysicalDiscrepancy>()
            : _discrepancyChecker.Compare(
                previousReading.PhysicalAttributes,
                physicalAttributes,
                _settings.DiscrepancyTolerance).ToArray();

        var reading = dossier.AppendReading(
            key,
            command.EdgeEventTime,
            new DeviceId(command.DeviceId),
            center,
            physicalAttributes,
            classification,
            command.EdgeEventTime,
            _timeProvider.GetUtcNow());
        foreach (var discrepancy in discrepancies)
        {
            reading.AddDiscrepancy(discrepancy);
        }

        var result = new InboundQueryResult(
            dossier.Barcode.Value,
            coreStatus,
            _statusMapper.Map(coreStatus),
            dossier.OriginCode.Value,
            dossier.DestinationCode.Value,
            reading.Id,
            discrepancies
                .Select(x => new DiscrepancyDto(x.Attribute, x.ReferenceValue, x.ObservedValue))
                .ToArray(),
            false);

        var idempotency = existingIdempotency ?? IdempotencyRecord.Start(key, requestHash, _timeProvider.GetUtcNow());
        idempotency.Complete(JsonSerializer.Serialize(result, JsonOptions), _timeProvider.GetUtcNow());
        if (isNewDossier)
        {
            _store.AddParcel(dossier);
        }
        else
        {
            // A child with an application-assigned identifier must be marked Added explicitly.
            _store.AddReading(reading);
        }

        if (existingIdempotency is null)
        {
            _store.AddIdempotency(idempotency);
        }

        _store.AddOutbox(OutboxMessage.Create(
            "parcel.lifecycle.reading-recorded",
            JsonSerializer.Serialize(new
            {
                ParcelBarcode = dossier.Barcode.Value,
                ReadingRecordId = reading.Id,
                Status = coreStatus,
                IdempotencyKey = key.Value
            }),
            new CorrelationId(command.CorrelationId),
            _timeProvider.GetUtcNow()));

        try
        {
            // Stateful synchronous acknowledgement occurs only after this DB commit succeeds.
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("The inbound request conflicts with an already committed operation.", exception);
        }
    }
}

public sealed class ParcelEventConsumer : IParcelEventConsumer
{
    private readonly IParcelLifecycleStore _store;
    private readonly IRequestHasher _requestHasher;
    private readonly IIdempotencyService _idempotencyService;
    private readonly TimeProvider _timeProvider;

    public ParcelEventConsumer(
        IParcelLifecycleStore store,
        IRequestHasher requestHasher,
        IIdempotencyService idempotencyService,
        TimeProvider timeProvider)
    {
        _store = store;
        _requestHasher = requestHasher;
        _idempotencyService = idempotencyService;
        _timeProvider = timeProvider;
    }

    public async Task<bool> ConsumeAsync(
        ParcelFinalEventCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.EventId == Guid.Empty || command.EdgeId == Guid.Empty)
        {
            throw new CoreValidationException("Event and Edge identifiers are required.");
        }

        // Correlation identifies one transport attempt and is intentionally excluded from
        // the durable business fingerprint. A retry may carry a fresh correlation ID.
        var hash = _requestHasher.Hash(new
        {
            command.EventId,
            command.EdgeId,
            command.ExchangeCenterCode,
            command.IdempotencyKey,
            command.ParcelBarcode,
            command.FinalStatus,
            command.OccurredAt
        });
        var inbox = await _store.FindInboxMessageAsync(command.EventId, cancellationToken).ConfigureAwait(false);
        if (inbox is not null)
        {
            if (!inbox.HasSamePayload(hash))
            {
                throw new CoreConflictException("The event identifier was reused with a different payload.");
            }

            return false;
        }

        var idempotencyKey = new IdempotencyKey(command.IdempotencyKey);
        var idempotency = await _store.FindIdempotencyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
        var decision = _idempotencyService.Evaluate(idempotency, hash);
        if (decision == IdempotencyDecision.Replay)
        {
            _store.AddInboxMessage(InboxMessage.Processed(command.EventId, hash, _timeProvider.GetUtcNow()));
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        if (decision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("The idempotent event is already being processed.");
        }

        var dossier = await _store.FindParcelAsync(new ParcelBarcode(command.ParcelBarcode), cancellationToken)
            .ConfigureAwait(false) ?? throw new CoreNotFoundException("Parcel dossier does not exist.");
        _ = new EdgeId(command.EdgeId);
        var eventCenter = new ExchangeCenterCode(command.ExchangeCenterCode);
        if (dossier.LatestReading()?.ExchangeCenterCode != eventCenter)
        {
            throw new CoreAuthorizationException(
                "The authenticated Edge center does not own the latest reading for this parcel.");
        }

        dossier.ApplyFinalStatus(command.FinalStatus);

        var record = idempotency ?? IdempotencyRecord.Start(idempotencyKey, hash, _timeProvider.GetUtcNow());
        record.Complete(command.FinalStatus.ToString(), _timeProvider.GetUtcNow());
        if (idempotency is null)
        {
            _store.AddIdempotency(record);
        }

        _store.AddInboxMessage(InboxMessage.Processed(command.EventId, hash, _timeProvider.GetUtcNow()));
        _store.AddOutbox(OutboxMessage.Create(
            "parcel.lifecycle.final-status-updated",
            JsonSerializer.Serialize(command),
            new CorrelationId(command.CorrelationId),
            command.OccurredAt));
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

public sealed class AttachmentLinkingService : IAttachmentLinkingService
{
    private readonly IParcelLifecycleStore _store;
    private readonly IRequestHasher _requestHasher;
    private readonly TimeProvider _timeProvider;

    public AttachmentLinkingService(
        IParcelLifecycleStore store,
        IRequestHasher requestHasher,
        TimeProvider timeProvider)
    {
        _store = store;
        _requestHasher = requestHasher;
        _timeProvider = timeProvider;
    }

    public async Task<bool> LinkAsync(ImageMetadataCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.EventId == Guid.Empty ||
            command.EdgeId == Guid.Empty ||
            command.ReadingRecordId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.ObjectKey) ||
            command.ObjectKey.Contains("..", StringComparison.Ordinal))
        {
            throw new CoreValidationException(
                "Valid event, reading, object key, content type, and attachment type are required.");
        }

        var contentType = AttachmentMetadataRules.NormalizeContentType(command.ContentType);
        var attachmentType = AttachmentMetadataRules.NormalizeAttachmentType(command.AttachmentType);

        var parcelBarcode = new ParcelBarcode(command.ParcelBarcode);
        // Correlation identifies one transport attempt and is intentionally excluded from
        // the durable business fingerprint. Every attachment business field remains covered.
        var hash = _requestHasher.Hash(new
        {
            command.EventId,
            command.EdgeId,
            command.ExchangeCenterCode,
            command.ParcelBarcode,
            command.ReadingRecordId,
            command.ObjectKey,
            ContentType = contentType,
            AttachmentType = attachmentType,
            command.ReceivedAt
        });
        var pendingByEvent = await _store.FindPendingAttachmentByEventAsync(
            command.EventId,
            cancellationToken).ConfigureAwait(false);
        if (pendingByEvent is not null && !pendingByEvent.HasSamePayload(hash))
        {
            throw new CoreConflictException(
                "The pending event identifier was reused with different attachment metadata.");
        }

        var inbox = await _store.FindInboxMessageAsync(command.EventId, cancellationToken).ConfigureAwait(false);
        if (inbox is not null)
        {
            if (!inbox.HasSamePayload(hash))
            {
                throw new CoreConflictException("The event identifier was reused with a different attachment payload.");
            }

            return false;
        }

        var expectedObjectKey = EdgeUploadObjectKey.Create(
            command.EdgeId,
            command.ExchangeCenterCode,
            command.ReadingRecordId,
            command.EventId);
        if (!string.Equals(command.ObjectKey, expectedObjectKey, StringComparison.Ordinal))
        {
            throw new CoreAuthorizationException(
                "The attachment object key does not belong to the declared Edge upload namespace.");
        }

        var reading = await _store.FindReadingAsync(command.ReadingRecordId, cancellationToken).ConfigureAwait(false);
        if (reading is null)
        {
            var pending = pendingByEvent ?? await _store.FindPendingAttachmentAsync(
                    command.ReadingRecordId,
                    command.ObjectKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (pending is null)
            {
                var createdAt = _timeProvider.GetUtcNow();
                _store.AddPendingAttachment(PendingAttachment.Create(
                    command.EventId,
                    hash,
                    JsonSerializer.Serialize(command),
                    parcelBarcode,
                    command.ReadingRecordId,
                    command.ObjectKey,
                    contentType,
                    attachmentType,
                    command.CorrelationId,
                    command.ReceivedAt,
                    createdAt));
                await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (pending.EventId != command.EventId || !pending.HasSamePayload(hash))
            {
                throw new CoreConflictException(
                    "The pending reading and object key are already associated with another attachment event.");
            }

            return false;
        }

        if (reading.ParcelBarcode != parcelBarcode)
        {
            throw new CoreValidationException("The attachment parcel does not own the specified reading.");
        }

        if (reading.ExchangeCenterCode != new ExchangeCenterCode(command.ExchangeCenterCode))
        {
            throw new CoreAuthorizationException(
                "The authenticated Edge center does not own the specified reading.");
        }

        var pendingAttachment = pendingByEvent ?? await _store.FindPendingAttachmentAsync(
                command.ReadingRecordId,
                command.ObjectKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (pendingAttachment is not null &&
            (pendingAttachment.EventId != command.EventId || !pendingAttachment.HasSamePayload(hash)))
        {
            throw new CoreConflictException(
                "The reading and object key are already associated with another attachment event.");
        }

        var attachment = reading.AddAttachment(
            command.ObjectKey,
            contentType,
            attachmentType,
            command.ReceivedAt);
        // The aggregate is already tracked, so explicitly establish this new child's state.
        _store.AddAttachment(attachment);
        if (pendingAttachment is not null)
        {
            _store.RemovePendingAttachment(pendingAttachment);
        }

        _store.AddInboxMessage(InboxMessage.Processed(command.EventId, hash, _timeProvider.GetUtcNow()));
        _store.AddOutbox(OutboxMessage.Create(
            "parcel.lifecycle.attachment-linked",
            JsonSerializer.Serialize(command),
            new CorrelationId(command.CorrelationId),
            _timeProvider.GetUtcNow()));
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}

public sealed class ParcelTrackingQueryService : IParcelTrackingQueryService
{
    private readonly IParcelLifecycleStore _store;

    public ParcelTrackingQueryService(IParcelLifecycleStore store) => _store = store;

    public async Task<ParcelTrackingDto?> FindAsync(
        string barcode,
        AccessScope accessScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessScope);
        var dossier = await _store.FindParcelAsync(new ParcelBarcode(barcode), cancellationToken).ConfigureAwait(false);
        if (dossier is null)
        {
            return null;
        }

        EnsureAccess(dossier, accessScope);
        return Map(dossier);
    }

    public async Task<IReadOnlyCollection<ParcelTrackingDto>> SearchAsync(
        AccessScope accessScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessScope);
        var centers = accessScope.IsGlobal
            ? null
            : accessScope.AllowedCenterCodes.Select(x => new ExchangeCenterCode(x)).ToArray();
        var dossiers = await _store.QueryParcelsAsync(centers, cancellationToken).ConfigureAwait(false);
        return dossiers.Select(Map).ToArray();
    }

    public async Task<PagedResult<ParcelTrackingDto>> SearchPageAsync(
        AccessScope accessScope,
        string? barcodeContains,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessScope);
        ValidatePage(page, pageSize);
        var filter = string.IsNullOrWhiteSpace(barcodeContains) ? null : barcodeContains.Trim();
        if (filter?.Length > 128)
        {
            throw new CoreValidationException("Parcel barcode filter cannot exceed 128 characters.");
        }

        var centers = accessScope.IsGlobal
            ? null
            : accessScope.AllowedCenterCodes.Select(x => new ExchangeCenterCode(x)).ToArray();
        var result = await _store.QueryParcelsPageAsync(
            centers,
            filter,
            checked((page - 1) * pageSize),
            pageSize,
            cancellationToken).ConfigureAwait(false);
        return new PagedResult<ParcelTrackingDto>(result.Items.Select(Map).ToArray(), result.TotalCount);
    }

    private static void EnsureAccess(ParcelDossier dossier, AccessScope scope)
    {
        if (scope.IsGlobal)
        {
            return;
        }

        var allowed = scope.AllowedCenterCodes.ToHashSet(StringComparer.Ordinal);
        var permitted = allowed.Contains(dossier.OriginCode.Value) ||
                        allowed.Contains(dossier.DestinationCode.Value) ||
                        dossier.Readings.Any(x => allowed.Contains(x.ExchangeCenterCode.Value));
        if (!permitted)
        {
            throw new CoreAuthorizationException("The caller is not allowed to view this parcel.");
        }
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page <= 0 || pageSize is <= 0 or > 200)
        {
            throw new CoreValidationException("Page must be positive and pageSize must be between 1 and 200.");
        }
    }

    private static ParcelTrackingDto Map(ParcelDossier dossier) =>
        new(
            dossier.Barcode.Value,
            dossier.ParcelType,
            dossier.ServiceType,
            dossier.OriginCode.Value,
            dossier.DestinationCode.Value,
            dossier.InboundFinalStatus,
            dossier.Readings.Count);
}

public sealed class BagStorageService : IBagStorageService
{
    private readonly IParcelLifecycleStore _store;
    private readonly IRequestHasher _hasher;
    private readonly IIdempotencyService _idempotencyService;
    private readonly TimeProvider _timeProvider;

    public BagStorageService(
        IParcelLifecycleStore store,
        IRequestHasher hasher,
        IIdempotencyService idempotencyService,
        TimeProvider timeProvider)
    {
        _store = store;
        _hasher = hasher;
        _idempotencyService = idempotencyService;
        _timeProvider = timeProvider;
    }

    public async Task<StorageResult> StoreAsync(BagStoreCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.MemberBarcodes.Count == 0 || command.MemberBarcodes.Distinct(StringComparer.Ordinal).Count() != command.MemberBarcodes.Count)
        {
            throw new CoreValidationException("A bag requires a non-empty set of unique parcel barcodes.");
        }

        var normalizedMembers = command.MemberBarcodes.Order(StringComparer.Ordinal).ToArray();
        var idempotencyKey = new IdempotencyKey(command.IdempotencyKey);
        var hash = _hasher.Hash(new
        {
            Operation = "bag.store.v1",
            IdempotencyKey = idempotencyKey.Value,
            command.BagBarcode,
            command.OriginCenterCode,
            command.DestinationCenterCode,
            command.CreatedByDeviceId,
            MemberBarcodes = normalizedMembers,
            command.SealNumber,
            command.TransportType,
            command.ClosedAt
        });
        var existingIdempotency = await _store.FindIdempotencyAsync(idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        var idempotencyDecision = _idempotencyService.Evaluate(existingIdempotency, hash);
        if (idempotencyDecision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("The same idempotent bag request is still in progress.");
        }

        var bagBarcode = new BagBarcode(command.BagBarcode);
        var existing = await _store.FindBagAsync(bagBarcode, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.IsReplay(hash))
            {
                throw new CoreConflictException("The bag identifier was already stored with a different payload.");
            }

        }

        var parcelBarcodes = normalizedMembers.Select(x => new ParcelBarcode(x)).ToArray();
        var dossiers = await _store.FindParcelsAsync(parcelBarcodes, cancellationToken).ConfigureAwait(false);
        if (dossiers.Count != parcelBarcodes.Length)
        {
            throw new CoreValidationException("Every bag member must reference an existing parcel dossier.");
        }

        var origin = new ExchangeCenterCode(command.OriginCenterCode);
        var destination = new ExchangeCenterCode(command.DestinationCenterCode);
        foreach (var dossier in dossiers)
        {
            if (dossier.LatestReading()?.ExchangeCenterCode != origin)
            {
                throw new CoreAuthorizationException(
                    "Every bag member's latest reading must be owned by the bag origin center.");
            }

            var effectiveDestination = dossier.OperationalDestinationCode ?? dossier.DestinationCode;
            if (effectiveDestination != destination)
            {
                throw new CoreValidationException("Every bag member must have the same destination as the bag.");
            }

            if (dossier.PhysicalBagReference is { } assigned && assigned != bagBarcode)
            {
                throw new CoreConflictException("A parcel cannot be a member of multiple bags.");
            }
        }

        if (idempotencyDecision == IdempotencyDecision.Replay)
        {
            return ReplayStorageResult(existingIdempotency!);
        }

        if (existing is not null)
        {
            var replay = new StorageResult(existing.Barcode.Value, true);
            await CompleteIdempotencyAsync(
                existingIdempotency,
                idempotencyKey,
                hash,
                replay,
                cancellationToken).ConfigureAwait(false);
            return replay;
        }

        var bag = Bag.Create(
            bagBarcode,
            origin,
            destination,
            new DeviceId(command.CreatedByDeviceId),
            parcelBarcodes,
            command.SealNumber,
            command.TransportType,
            command.ClosedAt,
            hash);
        foreach (var dossier in dossiers)
        {
            dossier.AssignPhysicalBag(bagBarcode);
        }

        _store.AddBag(bag);
        var result = new StorageResult(bag.Barcode.Value, false);
        CompleteIdempotency(existingIdempotency, idempotencyKey, hash, result);
        _store.AddOutbox(OutboxMessage.Create(
            "parcel.lifecycle.bag-stored",
            JsonSerializer.Serialize(command),
            new CorrelationId(command.CorrelationId),
            _timeProvider.GetUtcNow()));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("The bag request conflicts with an already committed operation.", exception);
        }
    }

    private async Task CompleteIdempotencyAsync(
        IdempotencyRecord? existing,
        IdempotencyKey key,
        string hash,
        StorageResult result,
        CancellationToken cancellationToken)
    {
        CompleteIdempotency(existing, key, hash, result);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CompleteIdempotency(
        IdempotencyRecord? existing,
        IdempotencyKey key,
        string hash,
        StorageResult result)
    {
        var record = existing ?? IdempotencyRecord.Start(key, hash, _timeProvider.GetUtcNow());
        record.Complete(JsonSerializer.Serialize(result), _timeProvider.GetUtcNow());
        if (existing is null)
        {
            _store.AddIdempotency(record);
        }
    }

    private static StorageResult ReplayStorageResult(IdempotencyRecord record)
    {
        var result = JsonSerializer.Deserialize<StorageResult>(record.ResponseSnapshot)
            ?? throw new CoreConflictException("Stored idempotent bag response is invalid.");
        return result with { IsReplay = true };
    }
}

public sealed class DispatchStorageService : IDispatchStorageService
{
    private readonly IParcelLifecycleStore _store;
    private readonly IRequestHasher _hasher;
    private readonly IIdempotencyService _idempotencyService;
    private readonly TimeProvider _timeProvider;

    public DispatchStorageService(
        IParcelLifecycleStore store,
        IRequestHasher hasher,
        IIdempotencyService idempotencyService,
        TimeProvider timeProvider)
    {
        _store = store;
        _hasher = hasher;
        _idempotencyService = idempotencyService;
        _timeProvider = timeProvider;
    }

    public async Task<StorageResult> StoreAsync(
        DispatchStoreCommand command,
        AccessScope accessScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(accessScope);
        if (command.BagBarcodes.Count == 0 || command.BagBarcodes.Distinct(StringComparer.Ordinal).Count() != command.BagBarcodes.Count)
        {
            throw new CoreValidationException("A dispatch requires a non-empty set of unique bag barcodes.");
        }

        var normalizedBags = command.BagBarcodes.Order(StringComparer.Ordinal).ToArray();
        var idempotencyKey = new IdempotencyKey(command.IdempotencyKey);
        var hash = _hasher.Hash(new
        {
            Operation = "dispatch.store.v1",
            IdempotencyKey = idempotencyKey.Value,
            command.DispatchId,
            command.DestinationCenterCode,
            BagBarcodes = normalizedBags,
            command.ClosedAt
        });
        var existingIdempotency = await _store.FindIdempotencyAsync(idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        var idempotencyDecision = _idempotencyService.Evaluate(existingIdempotency, hash);
        if (idempotencyDecision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("The same idempotent dispatch request is still in progress.");
        }

        var dispatchId = new DispatchId(command.DispatchId);
        var bagBarcodes = normalizedBags.Select(x => new BagBarcode(x)).ToArray();
        var bags = await _store.FindBagsAsync(bagBarcodes, cancellationToken).ConfigureAwait(false);
        if (bags.Count != bagBarcodes.Length)
        {
            throw new CoreValidationException("Every dispatch member must reference an existing bag.");
        }

        var destination = new ExchangeCenterCode(command.DestinationCenterCode);
        foreach (var bag in bags)
        {
            if (!accessScope.IsGlobal &&
                !accessScope.AllowedCenterCodes.Contains(bag.OriginCenter.Value, StringComparer.Ordinal))
            {
                throw new CoreAuthorizationException(
                    "The caller may dispatch only bags originated by one of its allowed centers.");
            }

            if (bag.DestinationCenter != destination)
            {
                throw new CoreValidationException("Every bag must have the same destination as the dispatch.");
            }

            if (bag.AssignedDispatchId is { } assigned && assigned != dispatchId)
            {
                throw new CoreConflictException("A bag cannot be assigned to multiple dispatches.");
            }
        }

        if (idempotencyDecision == IdempotencyDecision.Replay)
        {
            return ReplayStorageResult(existingIdempotency!);
        }

        var existing = await _store.FindDispatchAsync(dispatchId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.IsReplay(hash))
            {
                throw new CoreConflictException("The dispatch identifier was already stored with a different payload.");
            }

            var replay = new StorageResult(existing.Id.Value, true);
            await CompleteIdempotencyAsync(
                existingIdempotency,
                idempotencyKey,
                hash,
                replay,
                cancellationToken).ConfigureAwait(false);
            return replay;
        }

        var dispatch = Dispatch.Create(dispatchId, destination, bagBarcodes, command.ClosedAt, hash);
        foreach (var bag in bags)
        {
            bag.AssignToDispatch(dispatchId);
        }

        _store.AddDispatch(dispatch);
        var result = new StorageResult(dispatch.Id.Value, false);
        CompleteIdempotency(existingIdempotency, idempotencyKey, hash, result);
        _store.AddOutbox(OutboxMessage.Create(
            "parcel.lifecycle.dispatch-stored",
            JsonSerializer.Serialize(command),
            new CorrelationId(command.CorrelationId),
            _timeProvider.GetUtcNow()));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException(
                "The dispatch request conflicts with an already committed operation.",
                exception);
        }
    }

    private async Task CompleteIdempotencyAsync(
        IdempotencyRecord? existing,
        IdempotencyKey key,
        string hash,
        StorageResult result,
        CancellationToken cancellationToken)
    {
        CompleteIdempotency(existing, key, hash, result);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CompleteIdempotency(
        IdempotencyRecord? existing,
        IdempotencyKey key,
        string hash,
        StorageResult result)
    {
        var record = existing ?? IdempotencyRecord.Start(key, hash, _timeProvider.GetUtcNow());
        record.Complete(JsonSerializer.Serialize(result), _timeProvider.GetUtcNow());
        if (existing is null)
        {
            _store.AddIdempotency(record);
        }
    }

    private static StorageResult ReplayStorageResult(IdempotencyRecord record)
    {
        var result = JsonSerializer.Deserialize<StorageResult>(record.ResponseSnapshot)
            ?? throw new CoreConflictException("Stored idempotent dispatch response is invalid.");
        return result with { IsReplay = true };
    }
}

public sealed class BagDispatchQueryService(IParcelLifecycleStore store) : IBagDispatchQueryService
{
    public async Task<BagDto?> FindBagAsync(
        string bagBarcode,
        AccessScope accessScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessScope);
        var bag = await store.FindBagAsync(new BagBarcode(bagBarcode), cancellationToken).ConfigureAwait(false);
        if (bag is null || !CanAccess(accessScope, bag.OriginCenter, bag.DestinationCenter))
        {
            return null;
        }

        return new BagDto(
            bag.Barcode.Value,
            bag.OriginCenter.Value,
            bag.DestinationCenter.Value,
            bag.CreatedByDeviceId.Value,
            bag.Members.Select(x => x.ParcelBarcode.Value).ToArray(),
            bag.SealNumber,
            bag.TransportType,
            bag.ClosedAt,
            bag.State,
            bag.AssignedDispatchId?.Value);
    }

    public async Task<DispatchDto?> FindDispatchAsync(
        string dispatchId,
        AccessScope accessScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessScope);
        var dispatch = await store.FindDispatchAsync(new DispatchId(dispatchId), cancellationToken).ConfigureAwait(false);
        if (dispatch is null || !CanAccess(accessScope, dispatch.DestinationCenter))
        {
            return null;
        }

        return new DispatchDto(
            dispatch.Id.Value,
            dispatch.DestinationCenter.Value,
            dispatch.Bags.Select(x => x.BagBarcode.Value).ToArray(),
            dispatch.ClosedAt);
    }

    private static bool CanAccess(AccessScope scope, params ExchangeCenterCode[] centers) =>
        scope.IsGlobal || centers.Any(center =>
            scope.AllowedCenterCodes.Contains(center.Value, StringComparer.Ordinal));
}
