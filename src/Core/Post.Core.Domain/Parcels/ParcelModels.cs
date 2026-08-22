using Post.Core.Domain.Common;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Domain.Parcels;

public sealed class ParcelDossier : IAggregateRoot
{
    private readonly List<ReadingRecord> _readings = [];

    private ParcelDossier()
    {
        ParcelType = string.Empty;
        ServiceType = string.Empty;
    }

    private ParcelDossier(
        ParcelBarcode barcode,
        string parcelType,
        string serviceType,
        ExchangeCenterCode originCode,
        ExchangeCenterCode destinationCode)
    {
        Barcode = barcode;
        ParcelType = Required(parcelType, nameof(parcelType), 64);
        ServiceType = Required(serviceType, nameof(serviceType), 64);
        OriginCode = originCode;
        DestinationCode = destinationCode;
        ExportStatus = ParcelExportStatus.Unknown;
    }

    public ParcelBarcode Barcode { get; private set; }

    public string ParcelType { get; private set; }

    public string ServiceType { get; private set; }

    public ExchangeCenterCode OriginCode { get; private set; }

    public ExchangeCenterCode DestinationCode { get; private set; }

    public ExchangeCenterCode? OperationalDestinationCode { get; private set; }

    public CoreParcelStatus? InboundFinalStatus { get; private set; }

    public ParcelExportStatus ExportStatus { get; private set; }

    public BagBarcode? PhysicalBagReference { get; private set; }

    public IReadOnlyCollection<ReadingRecord> Readings => _readings.AsReadOnly();

    public static ParcelDossier Create(
        ParcelBarcode barcode,
        string parcelType,
        string serviceType,
        ExchangeCenterCode originCode,
        ExchangeCenterCode destinationCode) =>
        new(barcode, parcelType, serviceType, originCode, destinationCode);

    public ReadingRecord AppendReading(
        IdempotencyKey idempotencyKey,
        DateTimeOffset timestamp,
        DeviceId deviceId,
        ExchangeCenterCode exchangeCenterCode,
        PhysicalAttributes physicalAttributes,
        ReadingClassification classification,
        DateTimeOffset edgeEventTime,
        DateTimeOffset coreReceiveTime)
    {
        ArgumentNullException.ThrowIfNull(physicalAttributes);

        if (_readings.Any(x => x.IdempotencyKey == idempotencyKey))
        {
            throw new DomainRuleException("A reading with the same idempotency key already exists.");
        }

        if (coreReceiveTime < edgeEventTime - TimeSpan.FromDays(30))
        {
            throw new DomainRuleException("Core receive time is implausibly earlier than the edge event time.");
        }

        var previous = LatestReading(exchangeCenterCode);

        var reading = ReadingRecord.Create(
            Barcode,
            idempotencyKey,
            timestamp,
            deviceId,
            exchangeCenterCode,
            physicalAttributes,
            classification,
            previous?.Id,
            edgeEventTime,
            coreReceiveTime);

        _readings.Add(reading);
        return reading;
    }

    public ReadingRecord? LatestReading() => LatestReading(_readings);

    public ReadingRecord? LatestReading(ExchangeCenterCode center) =>
        LatestReading(_readings.Where(x => x.ExchangeCenterCode == center));

    public void ApplyFinalStatus(CoreParcelStatus status) => InboundFinalStatus = status;

    public void SetExportStatus(ParcelExportStatus status) => ExportStatus = status;

    public void SetOperationalDestination(ExchangeCenterCode destination) =>
        OperationalDestinationCode = destination;

    public void AssignPhysicalBag(BagBarcode bagBarcode)
    {
        if (PhysicalBagReference is { } existing && existing != bagBarcode)
        {
            throw new DomainRuleException("Parcel is already assigned to a different bag.");
        }

        PhysicalBagReference = bagBarcode;
    }

    private static ReadingRecord? LatestReading(IEnumerable<ReadingRecord> readings) =>
        readings
            .OrderByDescending(x => x.Timestamp)
            .ThenByDescending(x => x.CoreReceiveTime)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }
}

public sealed class ReadingRecord
{
    private readonly List<DiscrepancyRecord> _discrepancies = [];
    private readonly List<SupplementaryAttachment> _attachments = [];

    private ReadingRecord()
    {
        PhysicalAttributes = new PhysicalAttributes(0, 0, 0, 0);
    }

    private ReadingRecord(
        Guid id,
        ParcelBarcode parcelBarcode,
        IdempotencyKey idempotencyKey,
        DateTimeOffset timestamp,
        DeviceId deviceId,
        ExchangeCenterCode exchangeCenterCode,
        PhysicalAttributes physicalAttributes,
        ReadingClassification classification,
        Guid? previousReadingId,
        DateTimeOffset edgeEventTime,
        DateTimeOffset coreReceiveTime)
    {
        Id = id;
        ParcelBarcode = parcelBarcode;
        IdempotencyKey = idempotencyKey;
        Timestamp = timestamp;
        DeviceId = deviceId;
        ExchangeCenterCode = exchangeCenterCode;
        PhysicalAttributes = physicalAttributes;
        Classification = classification;
        PreviousReadingId = previousReadingId;
        EdgeEventTime = edgeEventTime;
        CoreReceiveTime = coreReceiveTime;
    }

    public Guid Id { get; private set; }

    public ParcelBarcode ParcelBarcode { get; private set; }

    public IdempotencyKey IdempotencyKey { get; private set; }

    public DateTimeOffset Timestamp { get; private set; }

    public DeviceId DeviceId { get; private set; }

    public ExchangeCenterCode ExchangeCenterCode { get; private set; }

    public PhysicalAttributes PhysicalAttributes { get; private set; }

    public ReadingClassification Classification { get; private set; }

    public Guid? PreviousReadingId { get; private set; }

    public DateTimeOffset EdgeEventTime { get; private set; }

    public DateTimeOffset CoreReceiveTime { get; private set; }

    public IReadOnlyCollection<DiscrepancyRecord> Discrepancies => _discrepancies.AsReadOnly();

    public IReadOnlyCollection<SupplementaryAttachment> Attachments => _attachments.AsReadOnly();

    internal static ReadingRecord Create(
        ParcelBarcode parcelBarcode,
        IdempotencyKey idempotencyKey,
        DateTimeOffset timestamp,
        DeviceId deviceId,
        ExchangeCenterCode exchangeCenterCode,
        PhysicalAttributes physicalAttributes,
        ReadingClassification classification,
        Guid? previousReadingId,
        DateTimeOffset edgeEventTime,
        DateTimeOffset coreReceiveTime) =>
        new(
            Guid.NewGuid(),
            parcelBarcode,
            idempotencyKey,
            timestamp,
            deviceId,
            exchangeCenterCode,
            physicalAttributes,
            classification,
            previousReadingId,
            edgeEventTime,
            coreReceiveTime);

    public void AddDiscrepancy(PhysicalDiscrepancy discrepancy)
    {
        ArgumentNullException.ThrowIfNull(discrepancy);
        _discrepancies.Add(DiscrepancyRecord.Create(Id, discrepancy));
    }

    public SupplementaryAttachment AddAttachment(
        string objectKey,
        string contentType,
        string attachmentType,
        DateTimeOffset receivedAt)
    {
        if (_attachments.Any(x => string.Equals(x.ObjectKey, objectKey, StringComparison.Ordinal)))
        {
            throw new DomainRuleException("The same object is already attached to this reading.");
        }

        var attachment = SupplementaryAttachment.Create(Id, objectKey, contentType, attachmentType, receivedAt);
        _attachments.Add(attachment);
        return attachment;
    }
}

public sealed class DiscrepancyRecord
{
    private DiscrepancyRecord()
    {
    }

    private DiscrepancyRecord(
        Guid id,
        Guid readingRecordId,
        PhysicalAttributeKind attribute,
        decimal referenceValue,
        decimal observedValue)
    {
        Id = id;
        ReadingRecordId = readingRecordId;
        Attribute = attribute;
        ReferenceValue = referenceValue;
        ObservedValue = observedValue;
    }

    public Guid Id { get; private set; }

    public Guid ReadingRecordId { get; private set; }

    public PhysicalAttributeKind Attribute { get; private set; }

    public decimal ReferenceValue { get; private set; }

    public decimal ObservedValue { get; private set; }

    internal static DiscrepancyRecord Create(Guid readingRecordId, PhysicalDiscrepancy discrepancy) =>
        new(Guid.NewGuid(), readingRecordId, discrepancy.Attribute, discrepancy.ReferenceValue, discrepancy.ObservedValue);
}

public sealed class SupplementaryAttachment
{
    public const int MaximumContentTypeLength = 128;
    public const int MaximumAttachmentTypeLength = 64;

    private SupplementaryAttachment()
    {
        ObjectKey = string.Empty;
        ContentType = string.Empty;
        AttachmentType = string.Empty;
    }

    private SupplementaryAttachment(
        Guid id,
        Guid readingRecordId,
        string objectKey,
        string contentType,
        string attachmentType,
        DateTimeOffset receivedAt)
    {
        Id = id;
        ReadingRecordId = readingRecordId;
        ObjectKey = Required(objectKey, nameof(objectKey), 512);
        ContentType = Required(contentType, nameof(contentType), MaximumContentTypeLength);
        AttachmentType = Required(attachmentType, nameof(attachmentType), MaximumAttachmentTypeLength);
        ReceivedAt = receivedAt;
    }

    public Guid Id { get; private set; }

    public Guid ReadingRecordId { get; private set; }

    public string ObjectKey { get; private set; }

    public string ContentType { get; private set; }

    public string AttachmentType { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    internal static SupplementaryAttachment Create(
        Guid readingRecordId,
        string objectKey,
        string contentType,
        string attachmentType,
        DateTimeOffset receivedAt) =>
        new(Guid.NewGuid(), readingRecordId, objectKey, contentType, attachmentType, receivedAt);

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }
}

public sealed class PendingAttachment
{
    private PendingAttachment()
    {
        RequestHash = string.Empty;
        PayloadJson = string.Empty;
        ObjectKey = string.Empty;
        ContentType = string.Empty;
        AttachmentType = string.Empty;
        CorrelationId = string.Empty;
    }

    private PendingAttachment(
        Guid id,
        Guid eventId,
        string requestHash,
        string payloadJson,
        ParcelBarcode parcelBarcode,
        Guid readingRecordId,
        string objectKey,
        string contentType,
        string attachmentType,
        string correlationId,
        DateTimeOffset receivedAt,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || eventId == Guid.Empty || readingRecordId == Guid.Empty)
        {
            throw new DomainRuleException("Pending attachment identifiers cannot be empty.");
        }

        Id = id;
        EventId = eventId;
        RequestHash = Required(requestHash, nameof(requestHash), 128);
        PayloadJson = Required(payloadJson, nameof(payloadJson), 32_768);
        ParcelBarcode = parcelBarcode;
        ReadingRecordId = readingRecordId;
        ObjectKey = Required(objectKey, nameof(objectKey), 512);
        ContentType = Required(
            contentType,
            nameof(contentType),
            SupplementaryAttachment.MaximumContentTypeLength);
        AttachmentType = Required(
            attachmentType,
            nameof(attachmentType),
            SupplementaryAttachment.MaximumAttachmentTypeLength);
        CorrelationId = Required(correlationId, nameof(correlationId), 256);
        ReceivedAt = receivedAt;
        CreatedAt = createdAt;
        CreatedAtUtcTicks = createdAt.UtcDateTime.Ticks;
        NextAttemptAt = createdAt;
        NextAttemptAtUtcTicks = CreatedAtUtcTicks;
    }

    public Guid Id { get; private set; }

    public Guid EventId { get; private set; }

    public string RequestHash { get; private set; }

    public string PayloadJson { get; private set; }

    public ParcelBarcode ParcelBarcode { get; private set; }

    public Guid ReadingRecordId { get; private set; }

    public string ObjectKey { get; private set; }

    public string ContentType { get; private set; }

    public string AttachmentType { get; private set; }

    public string CorrelationId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public long CreatedAtUtcTicks { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? NextAttemptAt { get; private set; }

    public long? NextAttemptAtUtcTicks { get; private set; }

    public DateTimeOffset? DeadLetteredAt { get; private set; }

    public long? DeadLetteredAtUtcTicks { get; private set; }

    public string? LastError { get; private set; }

    public static PendingAttachment Create(
        Guid eventId,
        string requestHash,
        string payloadJson,
        ParcelBarcode parcelBarcode,
        Guid readingRecordId,
        string objectKey,
        string contentType,
        string attachmentType,
        string correlationId,
        DateTimeOffset receivedAt,
        DateTimeOffset createdAt) =>
        new(
            Guid.NewGuid(),
            eventId,
            requestHash,
            payloadJson,
            parcelBarcode,
            readingRecordId,
            objectKey,
            contentType,
            attachmentType,
            correlationId,
            receivedAt,
            createdAt);

    public bool HasSamePayload(string requestHash) =>
        string.Equals(RequestHash, requestHash, StringComparison.Ordinal);

    public void ScheduleRetry(DateTimeOffset now, TimeSpan delay, string? error = null)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new DomainRuleException("Retry delay cannot be negative.");
        }

        if (DeadLetteredAt is not null)
        {
            throw new DomainRuleException("A dead-lettered attachment cannot be retried automatically.");
        }

        Attempts = checked(Attempts + 1);
        NextAttemptAt = now + delay;
        NextAttemptAtUtcTicks = NextAttemptAt.Value.UtcDateTime.Ticks;
        LastError = Optional(error, 2_048);
    }

    public bool IsExpired(DateTimeOffset now, TimeSpan maximumAge)
    {
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new DomainRuleException("Pending attachment maximum age must be positive.");
        }

        return now - CreatedAt >= maximumAge;
    }

    public void MarkDeadLettered(DateTimeOffset now, string error)
    {
        if (DeadLetteredAt is not null)
        {
            return;
        }

        DeadLetteredAt = now;
        DeadLetteredAtUtcTicks = now.UtcDateTime.Ticks;
        NextAttemptAt = null;
        NextAttemptAtUtcTicks = null;
        LastError = Required(error, nameof(error), 2_048);
    }

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }

    private static string? Optional(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Length <= maxLength
                ? value.Trim()
                : value.Trim()[..maxLength];
}

public sealed record PhysicalDiscrepancy(
    PhysicalAttributeKind Attribute,
    decimal ReferenceValue,
    decimal ObservedValue);

public sealed class Bag : IAggregateRoot
{
    private readonly List<BagMember> _members = [];

    private Bag()
    {
        SealNumber = string.Empty;
        TransportType = string.Empty;
        RequestHash = string.Empty;
    }

    private Bag(
        BagBarcode barcode,
        ExchangeCenterCode originCenter,
        ExchangeCenterCode destinationCenter,
        DeviceId createdByDeviceId,
        IEnumerable<ParcelBarcode> memberBarcodes,
        string sealNumber,
        string transportType,
        DateTimeOffset closedAt,
        string requestHash)
    {
        var members = memberBarcodes.Distinct().ToArray();
        if (members.Length == 0)
        {
            throw new DomainRuleException("A bag must contain at least one parcel.");
        }

        Barcode = barcode;
        OriginCenter = originCenter;
        DestinationCenter = destinationCenter;
        CreatedByDeviceId = createdByDeviceId;
        SealNumber = Required(sealNumber, nameof(sealNumber), 128);
        TransportType = Required(transportType, nameof(transportType), 64);
        ClosedAt = closedAt;
        State = BagState.Closed;
        RequestHash = Required(requestHash, nameof(requestHash), 128);
        _members.AddRange(members.Select(x => BagMember.Create(barcode, x)));
    }

    public BagBarcode Barcode { get; private set; }

    public ExchangeCenterCode OriginCenter { get; private set; }

    public ExchangeCenterCode DestinationCenter { get; private set; }

    public DeviceId CreatedByDeviceId { get; private set; }

    public string SealNumber { get; private set; }

    public string TransportType { get; private set; }

    public DateTimeOffset ClosedAt { get; private set; }

    public BagState State { get; private set; }

    public DispatchId? AssignedDispatchId { get; private set; }

    public string RequestHash { get; private set; }

    public IReadOnlyCollection<BagMember> Members => _members.AsReadOnly();

    public static Bag Create(
        BagBarcode barcode,
        ExchangeCenterCode originCenter,
        ExchangeCenterCode destinationCenter,
        DeviceId createdByDeviceId,
        IEnumerable<ParcelBarcode> memberBarcodes,
        string sealNumber,
        string transportType,
        DateTimeOffset closedAt,
        string requestHash) =>
        new(
            barcode,
            originCenter,
            destinationCenter,
            createdByDeviceId,
            memberBarcodes,
            sealNumber,
            transportType,
            closedAt,
            requestHash);

    public bool IsReplay(string requestHash) =>
        string.Equals(RequestHash, requestHash, StringComparison.Ordinal);

    public void AssignToDispatch(DispatchId dispatchId)
    {
        if (AssignedDispatchId is { } existing && existing != dispatchId)
        {
            throw new DomainRuleException("Bag is already assigned to another dispatch.");
        }

        AssignedDispatchId = dispatchId;
        State = BagState.AssignedToDispatch;
    }

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }
}

public sealed class BagMember
{
    private BagMember()
    {
    }

    private BagMember(BagBarcode bagBarcode, ParcelBarcode parcelBarcode)
    {
        BagBarcode = bagBarcode;
        ParcelBarcode = parcelBarcode;
    }

    public BagBarcode BagBarcode { get; private set; }

    public ParcelBarcode ParcelBarcode { get; private set; }

    internal static BagMember Create(BagBarcode bagBarcode, ParcelBarcode parcelBarcode) =>
        new(bagBarcode, parcelBarcode);
}

public sealed class Dispatch : IAggregateRoot
{
    private readonly List<DispatchBag> _bags = [];

    private Dispatch()
    {
        RequestHash = string.Empty;
    }

    private Dispatch(
        DispatchId id,
        ExchangeCenterCode destinationCenter,
        IEnumerable<BagBarcode> bags,
        DateTimeOffset closedAt,
        string requestHash)
    {
        var distinctBags = bags.Distinct().ToArray();
        if (distinctBags.Length == 0)
        {
            throw new DomainRuleException("A dispatch must contain at least one bag.");
        }

        Id = id;
        DestinationCenter = destinationCenter;
        ClosedAt = closedAt;
        RequestHash = string.IsNullOrWhiteSpace(requestHash)
            ? throw new DomainRuleException("Request hash is required.")
            : requestHash;
        _bags.AddRange(distinctBags.Select(x => DispatchBag.Create(id, x)));
    }

    public DispatchId Id { get; private set; }

    public ExchangeCenterCode DestinationCenter { get; private set; }

    public DateTimeOffset ClosedAt { get; private set; }

    public string RequestHash { get; private set; }

    public IReadOnlyCollection<DispatchBag> Bags => _bags.AsReadOnly();

    public static Dispatch Create(
        DispatchId id,
        ExchangeCenterCode destinationCenter,
        IEnumerable<BagBarcode> bags,
        DateTimeOffset closedAt,
        string requestHash) =>
        new(id, destinationCenter, bags, closedAt, requestHash);

    public bool IsReplay(string requestHash) =>
        string.Equals(RequestHash, requestHash, StringComparison.Ordinal);
}

public sealed class DispatchBag
{
    private DispatchBag()
    {
    }

    private DispatchBag(DispatchId dispatchId, BagBarcode bagBarcode)
    {
        DispatchId = dispatchId;
        BagBarcode = bagBarcode;
    }

    public DispatchId DispatchId { get; private set; }

    public BagBarcode BagBarcode { get; private set; }

    internal static DispatchBag Create(DispatchId dispatchId, BagBarcode bagBarcode) =>
        new(dispatchId, bagBarcode);
}
