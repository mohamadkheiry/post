using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Common;
using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;
using Post.Contracts;
using Post.Contracts.V1.EdgeCore;

namespace Post.Edge.Application.Services;

public sealed class InboundRegistrationService(
    IEdgeParcelRepository parcels,
    IPendingInboundRepository pending,
    IInboundEvidenceRepository evidenceRepository,
    IDeviceConfigurationRepository configurations,
    IDestinationLookupClient destinationLookup,
    ICoreApiClient core,
    IPostalInboundClient postal,
    IOperationJournal operations,
    IExternalOperationSagaRepository externalOperations,
    IOutboxWriter outbox,
    IEdgeUnitOfWork unitOfWork,
    IClock clock) : IInboundRegistrationService
{
    private const string OperationType = "parcel.inbound";
    private static readonly TimeSpan DefaultPostalTimeout = TimeSpan.FromSeconds(8);

    public async Task<InboundParcelResponse> RegisterAsync(
        InboundParcelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = OperationIdentity.Resolve(request.OperationId, request.CorrelationId);
        var requestHash = Hash(request);
        var cached = await operations.GetAsync<InboundParcelResponse>(
            identity.Value, OperationType, cancellationToken);
        if (cached is not null)
        {
            return string.Equals(cached.RequestHash, requestHash, StringComparison.Ordinal)
                ? cached.Response
                : new InboundParcelResponse(
                    request.CorrelationId,
                    identity.Value,
                    null,
                    InboundResultStatus.Error,
                    string.Empty,
                    null,
                    null,
                    ErrorCode: "IDEMPOTENCY_CONFLICT",
                    ErrorMessage: "The operation id was already used for a different inbound request.",
                    OperationIdIsBestEffort: identity.IsBestEffortFallback);
        }
        var existingExternal = await externalOperations.GetAsync(
            identity.Value,
            OperationType,
            cancellationToken);
        if (existingExternal is not null &&
            !string.Equals(existingExternal.RequestHash, requestHash, StringComparison.Ordinal))
        {
            return await StoreAsync(new InboundParcelResponse(
                request.CorrelationId,
                identity.Value,
                null,
                InboundResultStatus.Error,
                string.Empty,
                null,
                null,
                ErrorCode: "IDEMPOTENCY_CONFLICT",
                ErrorMessage: "The operation id was already used for a different inbound request.",
                OperationIdIsBestEffort: identity.IsBestEffortFallback), requestHash, cancellationToken);
        }

        BarcodeSet barcodeSet;
        PhysicalAttributes physical;
        ValidatedEvidence evidence;
        TimeSpan postalTimeout;
        try
        {
            barcodeSet = BarcodeSet.Create(request.Barcodes);
            physical = BuildPhysical(request);
            evidence = ValidateEvidence(request);
            postalTimeout = ResolveTimeout(request);
        }
        catch (Exception ex) when (ex is ArgumentException or DomainException or OverflowException)
        {
            return await StoreAsync(new InboundParcelResponse(
                request.CorrelationId,
                identity.Value,
                null,
                InboundResultStatus.Error,
                string.Empty,
                null,
                null,
                ErrorCode: "INVALID_PARCEL",
                ErrorMessage: ex.Message,
                OperationIdIsBestEffort: identity.IsBestEffortFallback), requestHash, cancellationToken);
        }

        var canonical = barcodeSet.CanonicalBarcode;
        string originCode;
        if (canonical.Kind == BarcodeKind.FourteenDigit)
        {
            var configuration = await configurations.GetAsync(cancellationToken);
            if (configuration is null)
            {
                return await StoreAsync(new InboundParcelResponse(
                    request.CorrelationId,
                    identity.Value,
                    null,
                    InboundResultStatus.Error,
                    canonical.Canonical,
                    null,
                    null,
                    ErrorCode: "CONFIGURATION_UNAVAILABLE",
                    ErrorMessage: "The local Edge configuration is unavailable.",
                    OperationIdIsBestEffort: identity.IsBestEffortFallback), requestHash, cancellationToken);
            }
            originCode = configuration.ExchangeCenterCode;
        }
        else
        {
            originCode = canonical.EmbeddedOriginCode
                         ?? throw new InvalidOperationException(
                             "The canonical postal barcode omitted its embedded origin.");
        }
        var destination = canonical.EmbeddedDestinationCode;
        if (canonical.Kind == BarcodeKind.FourteenDigit)
        {
            destination = await destinationLookup.ResolveAsync(canonical.Canonical, cancellationToken);
        }

        PostalCenterCode destinationCode;
        try
        {
            destinationCode = PostalCenterCode.Create(destination ?? string.Empty);
        }
        catch (DomainException ex)
        {
            return await StoreAsync(new InboundParcelResponse(
                request.CorrelationId,
                identity.Value,
                null,
                InboundResultStatus.Error,
                canonical.Canonical,
                originCode,
                null,
                ErrorCode: "DESTINATION_UNRESOLVED",
                ErrorMessage: ex.Message,
                OperationIdIsBestEffort: identity.IsBestEffortFallback), requestHash, cancellationToken);
        }

        var recoveredIntent = existingExternal is null
            ? null
            : ApplicationSupport.TryFromJson<InboundSagaIntent>(existingExternal.IntentJson);
        if (existingExternal is not null &&
            (existingExternal.Finalized || recoveredIntent is null || recoveredIntent.ReadId == Guid.Empty))
        {
            return await StoreAsync(new InboundParcelResponse(
                request.CorrelationId,
                identity.Value,
                recoveredIntent?.ReadId,
                InboundResultStatus.Error,
                canonical.Canonical,
                originCode,
                destinationCode.Value,
                ErrorCode: "MANUAL_RECONCILIATION_REQUIRED",
                ErrorMessage: "A prior Postal inbound call has no safely recoverable intent and will not be repeated.",
                OperationIdIsBestEffort: identity.IsBestEffortFallback), requestHash, cancellationToken);
        }

        var readId = recoveredIntent?.ReadId ?? Guid.NewGuid();
        if (evidence.HasContent && existingExternal is null)
        {
            evidenceRepository.Add(new InboundEvidence(
                readId,
                evidence.SupplementaryDataJson,
                evidence.Images,
                clock.UtcNow));
        }

        var coreRequest = new CoreInboundLookupRequest(
            identity.Value,
            request.DeviceId,
            barcodeSet.Aliases,
            request.ParcelType ?? "Unspecified",
            request.ServiceType ?? "Unspecified",
            originCode,
            destinationCode.Value,
            physical.WeightGrams,
            physical.Dimensions?.LengthMm,
            physical.Dimensions?.WidthMm,
            physical.Dimensions?.HeightMm,
            request.ReadTimestamp,
            request.CorrelationId.ToString("D"));
        CoreInboundLookupResult coreResult;
        var coreLookupDeferred = false;
        try
        {
            coreResult = await core.LookupInboundAsync(coreRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            coreLookupDeferred = true;
            coreResult = new CoreInboundLookupResult(CoreParcelDisposition.Normal, null, destinationCode.Value);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            coreLookupDeferred = true;
            coreResult = new CoreInboundLookupResult(CoreParcelDisposition.Normal, null, destinationCode.Value);
        }

        if (!coreLookupDeferred)
        {
            EnqueueAttachments(identity.Value, readId, canonical.Canonical, coreResult.ReadingRecordId ?? readId, evidence);
            if (existingExternal is null &&
                coreResult.Disposition is CoreParcelDisposition.Returned or CoreParcelDisposition.Refused)
            {
                return await StoreAsync(new InboundParcelResponse(
                    request.CorrelationId,
                    identity.Value,
                    readId,
                    coreResult.Disposition == CoreParcelDisposition.Returned
                        ? InboundResultStatus.Returned
                        : InboundResultStatus.Refused,
                    canonical.Canonical,
                    originCode,
                    PostalCenterCode.Create(coreResult.DestinationCode ?? destinationCode.Value).Value,
                    coreResult.DiscrepancyCode,
                    OperationIdIsBestEffort: identity.IsBestEffortFallback), requestHash, cancellationToken);
            }
        }

        var previous = await parcels.FindByCanonicalBarcodeAsync(canonical.Canonical, cancellationToken);
        var command = new PostalInboundCommand(
            identity.Value,
            readId,
            request.DeviceId,
            barcodeSet,
            destinationCode.Value,
            physical,
            request.ParcelType,
            request.ServiceType,
            request.ReadTimestamp,
            postalTimeout);

        PostalInboundResult postalResult;
        bool postalOutcomeUnknown;
        DateTimeOffset postalCalledAt;
        if (existingExternal is null)
        {
            externalOperations.AddIntent(new ExternalOperationSaga(
                identity.Value,
                OperationType,
                requestHash,
                ApplicationSupport.ToJson(new InboundSagaIntent(readId)),
                null,
                false,
                clock.UtcNow,
                clock.UtcNow));
            await unitOfWork.SaveChangesAsync(cancellationToken);

            postalOutcomeUnknown = false;
            postalCalledAt = clock.UtcNow;
            try
            {
                postalResult = await postal.RegisterAsync(command, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                postalOutcomeUnknown = true;
                postalResult = new PostalInboundResult(
                    PostalInboundStatus.Pending,
                    identity.Value.ToString("N"),
                    "POSTAL_UNAVAILABLE",
                    "Postal registration timed out; its commit outcome is unknown.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                postalOutcomeUnknown = true;
                postalResult = new PostalInboundResult(
                    PostalInboundStatus.Pending,
                    identity.Value.ToString("N"),
                    "POSTAL_UNAVAILABLE",
                    "Postal registration is unavailable; its commit outcome is unknown.");
            }

            await externalOperations.StoreReceiptAsync(
                identity.Value,
                OperationType,
                ApplicationSupport.ToJson(postalResult),
                clock.UtcNow,
                cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        else
        {
            postalResult = ApplicationSupport.TryFromJson<PostalInboundResult>(existingExternal.ReceiptJson)
                           ?? new PostalInboundResult(
                               PostalInboundStatus.Pending,
                               null,
                               "POSTAL_OUTCOME_UNKNOWN",
                               "The durable Postal inbound receipt is unavailable.");
            postalOutcomeUnknown =
                string.Equals(postalResult.ErrorCode, "POSTAL_UNAVAILABLE", StringComparison.Ordinal) ||
                string.Equals(postalResult.ErrorCode, "POSTAL_OUTCOME_UNKNOWN", StringComparison.Ordinal);
            postalCalledAt = existingExternal.CreatedAt;
        }

        outbox.Enqueue(ApplicationSupport.OperationalResult(
            clock,
            "inbound",
            identity.Value,
            canonical.Canonical,
            $"PostalInbound.{postalResult.Status}",
            postalResult.Status == PostalInboundStatus.Registered,
            postalResult.ErrorCode,
            postalResult.ErrorMessage,
            postalCalledAt,
            1,
            postalResult.Status switch
            {
                PostalInboundStatus.Registered => "Succeeded",
                PostalInboundStatus.Pending => "Pending",
                _ => "Failed"
            }));

        if (postalOutcomeUnknown)
        {
            return await StoreAsync(new InboundParcelResponse(
                request.CorrelationId,
                identity.Value,
                readId,
                InboundResultStatus.Error,
                canonical.Canonical,
                originCode,
                destinationCode.Value,
                coreResult.DiscrepancyCode,
                "MANUAL_RECONCILIATION_REQUIRED",
                "The Postal inbound outcome is unknown and the non-idempotent operation will not be repeated.",
                identity.IsBestEffortFallback), requestHash, cancellationToken);
        }

        if (postalResult.Status == PostalInboundStatus.Failed)
        {
            return await StoreAsync(new InboundParcelResponse(
                request.CorrelationId,
                identity.Value,
                readId,
                InboundResultStatus.Error,
                canonical.Canonical,
                originCode,
                destinationCode.Value,
                coreResult.DiscrepancyCode,
                postalResult.ErrorCode ?? "POSTAL_REJECTED",
                "Postal inbound registration failed.",
                identity.IsBestEffortFallback), requestHash, cancellationToken, true);
        }

        var postalPending = postalResult.Status == PostalInboundStatus.Pending;
        var isPending = postalPending || coreLookupDeferred;
        var parcel = EdgeParcel.Create(
            readId,
            barcodeSet,
            request.DeviceId,
            request.ReadTimestamp,
            physical,
            request.ParcelType,
            request.ServiceType,
            isPending,
            previous?.ReadId);
        parcels.Add(parcel);

        if (postalPending)
        {
            var trackingId = string.IsNullOrWhiteSpace(postalResult.TrackingId)
                ? identity.Value.ToString("N")
                : postalResult.TrackingId;
            pending.Add(new PendingInboundItem(identity.Value, readId, trackingId, 0, clock.UtcNow, postalResult.ErrorMessage));
        }
        if (coreLookupDeferred)
        {
            outbox.Enqueue(ApplicationSupport.Outbox(
                clock,
                "CoreLookup",
                "EdgeInboundLookupRequested",
                new DeferredCoreInboundLookup(
                    coreRequest,
                    readId,
                    requestHash,
                    canonical.Canonical,
                    originCode,
                    destinationCode.Value,
                    evidence.Images.Select(image => image.ImageId).ToArray(),
                    evidence.SupplementaryDataJson is not null,
                    clock.UtcNow),
                $"core-lookup:{identity.Value:N}"));
        }
        else if (!postalPending)
        {
            outbox.Enqueue(ApplicationSupport.Outbox(
                clock,
                "Core",
                ContractMessageTypes.EdgeParcelReadingSubmitted,
                new ParcelReadingSubmittedV1(
                    identity.Value,
                    parcel.CanonicalBarcode,
                    parcel.Aliases,
                    parcel.DeviceId,
                    parcel.ReadTimestamp,
                    ToCorePhysical(parcel.PhysicalAttributes),
                    CoreOperationStatusV1.Succeeded,
                    null,
                    originCode,
                    destinationCode.Value),
                $"inbound:{identity.Value:N}"));
        }

        return await StoreAsync(new InboundParcelResponse(
            request.CorrelationId,
            identity.Value,
            readId,
            isPending ? InboundResultStatus.Pending : InboundResultStatus.Registered,
            canonical.Canonical,
            originCode,
            destinationCode.Value,
            coreResult.DiscrepancyCode,
            OperationIdIsBestEffort: identity.IsBestEffortFallback), requestHash, cancellationToken, true);
    }

    private void EnqueueAttachments(
        Guid operationId,
        Guid readId,
        string parcelBarcode,
        Guid readingRecordId,
        ValidatedEvidence evidence)
    {
        if (evidence.SupplementaryDataJson is not null)
        {
            outbox.Enqueue(ApplicationSupport.Outbox(
                clock,
                "SupplementaryUpload",
                "EdgeParcelSupplementaryDataUploadRequested",
                new { ReadId = readId, ReadingRecordId = readingRecordId, ParcelBarcode = parcelBarcode, CapturedAt = clock.UtcNow },
                $"supplementary:{operationId:N}"));
        }
        foreach (var image in evidence.Images)
        {
            outbox.Enqueue(ApplicationSupport.Outbox(
                clock,
                "ImageUpload",
                "EdgeParcelImageUploadRequested",
                new { ReadId = readId, ReadingRecordId = readingRecordId, ParcelBarcode = parcelBarcode, image.ImageId, CapturedAt = clock.UtcNow },
                $"image:{operationId:N}:{image.ImageId}"));
        }
    }

    private async Task<InboundParcelResponse> StoreAsync(
        InboundParcelResponse response,
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

    private static string Hash(InboundParcelRequest request) => ApplicationSupport.RequestHash(new
    {
        request.DeviceId,
        Barcodes = request.Barcodes.Order(StringComparer.Ordinal).ToArray(),
        request.ReadTimestamp,
        request.Weight,
        request.Length,
        request.Width,
        request.Height,
        request.ParcelType,
        request.ServiceType,
        SupplementaryData = request.SupplementaryData?.GetRawText(),
        Images = request.Images?.OrderBy(image => image?.ImageId, StringComparer.Ordinal).ToArray(),
        request.PostalTimeout,
        request.TimeoutMs
    });

    private static PhysicalAttributes BuildPhysical(InboundParcelRequest request)
    {
        int? weight = request.Weight is null ? null : checked((int)Math.Round(request.Weight.Value));
        ParcelDimensions? dimensions = null;
        var suppliedDimensions = new[] { request.Length, request.Width, request.Height }.Count(x => x is not null);
        if (suppliedDimensions is > 0 and < 3)
        {
            throw new DomainException("Dimensions must be supplied together.");
        }

        if (suppliedDimensions == 3)
        {
            dimensions = new ParcelDimensions(
                checked((int)Math.Round(request.Length!.Value)),
                checked((int)Math.Round(request.Width!.Value)),
                checked((int)Math.Round(request.Height!.Value)));
        }

        return new PhysicalAttributes(weight, dimensions);
    }

    private static TimeSpan ResolveTimeout(InboundParcelRequest request)
    {
        if (request.TimeoutMs is not null)
        {
            if (request.TimeoutMs is <= 0 or > 120_000)
                throw new DomainException("TimeoutMs must be between 1 and 120000 milliseconds.");
            return TimeSpan.FromMilliseconds(request.TimeoutMs.Value);
        }

        if (request.PostalTimeout is { } timeout)
        {
            if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
                throw new DomainException("Postal timeout must be between 1 millisecond and 2 minutes.");
            return timeout;
        }

        return DefaultPostalTimeout;
    }

    private static CorePhysicalAttributesV1 ToCorePhysical(PhysicalAttributes physical) => new(
        physical.WeightGrams,
        physical.Dimensions?.LengthMm,
        physical.Dimensions?.WidthMm,
        physical.Dimensions?.HeightMm);

    private static ValidatedEvidence ValidateEvidence(InboundParcelRequest request)
    {
        string? supplementaryJson = null;
        if (request.SupplementaryData is { } supplementary)
        {
            if (supplementary.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new DomainException("Supplementary data must be a JSON object.");
            supplementaryJson = supplementary.GetRawText();
            if (System.Text.Encoding.UTF8.GetByteCount(supplementaryJson) > 262_144)
                throw new DomainException("Supplementary data exceeds the 256 KiB safety limit.");
        }

        var sourceImages = request.Images ?? [];
        if (sourceImages.Count > InboundEvidenceLimits.MaximumImageCount)
            throw new DomainException("At most 32 images are accepted per inbound read.");
        var images = new List<InboundImageContent>(sourceImages.Count);
        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        long aggregateBase64Characters = 0;
        long aggregateDecodedBytes = 0;
        foreach (var image in sourceImages)
        {
            if (image is null) throw new DomainException("Inbound images cannot contain null items.");
            if (string.IsNullOrWhiteSpace(image.ImageId) || image.ImageId.Length > 128 || !imageIds.Add(image.ImageId))
                throw new DomainException("Image ids must be unique, non-empty, and at most 128 characters.");
            if (string.IsNullOrWhiteSpace(image.ContentBase64))
                throw new DomainException("Image content is required.");
            if (image.ContentBase64.Length > InboundEvidenceLimits.MaximumImageBase64Characters)
                throw new DomainException("An image exceeds the 5,000,000-character Base64 safety limit.");
            aggregateBase64Characters += image.ContentBase64.Length;
            if (aggregateBase64Characters > InboundEvidenceLimits.MaximumAggregateImageBase64Characters)
                throw new DomainException("Aggregate image content exceeds the Base64 safety limit.");
            byte[] content;
            try
            {
                content = Convert.FromBase64String(image.ContentBase64);
            }
            catch (FormatException)
            {
                throw new DomainException("Image content is not valid Base64.");
            }
            if (content.Length > InboundEvidenceLimits.MaximumDecodedImageBytes)
                throw new DomainException("Decoded image exceeds the 3.75 MB safety limit.");
            aggregateDecodedBytes += content.Length;
            if (aggregateDecodedBytes > InboundEvidenceLimits.MaximumAggregateDecodedImageBytes)
                throw new DomainException("Aggregate decoded image content exceeds the safety limit.");
            if (image.MimeType is { Length: > 255 } || image.MimeType?.IndexOfAny(['\r', '\n']) >= 0)
                throw new DomainException("Image MIME type is invalid.");
            if (image.Description is { Length: > 1_024 }) throw new DomainException("Image description is too long.");
            images.Add(new InboundImageContent(
                image.ImageId,
                content,
                string.IsNullOrWhiteSpace(image.MimeType) ? null : image.MimeType.Trim(),
                string.IsNullOrWhiteSpace(image.Description) ? null : image.Description.Trim()));
        }
        return new ValidatedEvidence(supplementaryJson, images);
    }

    private sealed record ValidatedEvidence(
        string? SupplementaryDataJson,
        IReadOnlyCollection<InboundImageContent> Images)
    {
        internal bool HasContent => SupplementaryDataJson is not null || Images.Count > 0;
    }

    private sealed record InboundSagaIntent(Guid ReadId);
}
