using Post.Edge.Domain.Common;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Domain.Bags;

public sealed class EdgeBag
{
    private readonly string[] _memberBarcodes;
    private readonly List<ParcelError> _errors;

    private EdgeBag(
        string bagBarcode,
        Guid localOperationId,
        string deviceId,
        string destinationCenterCode,
        IEnumerable<string> memberBarcodes,
        byte[] bagLabel,
        string sealNumber,
        string transportType,
        BagCounts counts,
        IEnumerable<ParcelError>? errors,
        DateTimeOffset createdAt,
        string? dispatchId,
        bool coreConfirmed)
    {
        if (string.IsNullOrWhiteSpace(bagBarcode)) throw new DomainException("Final bag barcode is required.");
        if (localOperationId == Guid.Empty) throw new DomainException("Local operation id is required.");
        if (string.IsNullOrWhiteSpace(deviceId)) throw new DomainException("Device id is required.");
        if (string.IsNullOrWhiteSpace(destinationCenterCode)) throw new DomainException("Destination is required.");
        if (bagLabel is null || bagLabel.Length == 0) throw new DomainException("Final bag label is required.");
        if (string.IsNullOrWhiteSpace(sealNumber)) throw new DomainException("Seal number is required.");
        if (string.IsNullOrWhiteSpace(transportType)) throw new DomainException("Transport type is required.");
        ArgumentNullException.ThrowIfNull(counts);

        _memberBarcodes = memberBarcodes.Distinct(StringComparer.Ordinal).ToArray();
        if (_memberBarcodes.Length == 0 || _memberBarcodes.Length != counts.SuccessCount)
        {
            throw new DomainException("Final bag members must exactly match the successful counter total.");
        }

        BagBarcode = bagBarcode.Trim();
        LocalOperationId = localOperationId;
        DeviceId = deviceId.Trim();
        DestinationCenterCode = destinationCenterCode.Trim();
        BagLabel = bagLabel.ToArray();
        SealNumber = sealNumber.Trim();
        TransportType = transportType.Trim();
        Counts = counts;
        _errors = errors?.ToList() ?? [];
        CreatedAt = createdAt;
        DispatchId = string.IsNullOrWhiteSpace(dispatchId) ? null : dispatchId.Trim();
        CoreConfirmed = coreConfirmed;
    }

    public string BagBarcode { get; }
    public Guid LocalOperationId { get; }
    public string DeviceId { get; }
    public string DestinationCenterCode { get; }
    public IReadOnlyList<string> MemberBarcodes => _memberBarcodes;
    public byte[] BagLabel { get; }
    public string SealNumber { get; }
    public string TransportType { get; }
    public BagCounts Counts { get; }
    public IReadOnlyList<ParcelError> Errors => _errors;
    public DateTimeOffset CreatedAt { get; }
    public string? DispatchId { get; private set; }
    public bool DispatchAssigned => DispatchId is not null;
    public bool CoreConfirmed { get; private set; }

    public static EdgeBag Create(
        string bagBarcode,
        Guid localOperationId,
        string deviceId,
        PostalCenterCode destination,
        IEnumerable<string> memberBarcodes,
        byte[] bagLabel,
        string sealNumber,
        string transportType,
        BagCounts counts,
        IEnumerable<ParcelError>? errors,
        DateTimeOffset createdAt) => new(
            bagBarcode, localOperationId, deviceId, destination.Value, memberBarcodes, bagLabel,
            sealNumber, transportType, counts, errors, createdAt, null, false);

    public static EdgeBag Rehydrate(
        string bagBarcode,
        Guid localOperationId,
        string deviceId,
        string destinationCenterCode,
        IEnumerable<string> memberBarcodes,
        byte[] bagLabel,
        string sealNumber,
        string transportType,
        BagCounts counts,
        IEnumerable<ParcelError>? errors,
        DateTimeOffset createdAt,
        string? dispatchId,
        bool coreConfirmed) => new(
            bagBarcode, localOperationId, deviceId, destinationCenterCode, memberBarcodes, bagLabel,
            sealNumber, transportType, counts, errors, createdAt, dispatchId, coreConfirmed);

    public void AssignDispatch(string dispatchId)
    {
        if (string.IsNullOrWhiteSpace(dispatchId)) throw new DomainException("Dispatch id is required.");
        var normalized = dispatchId.Trim();
        if (DispatchId is not null && !string.Equals(DispatchId, normalized, StringComparison.Ordinal))
        {
            throw new DomainException("A final bag cannot be reassigned to another dispatch.");
        }

        DispatchId = normalized;
    }

    public void MarkCoreConfirmed() => CoreConfirmed = true;
}

public enum PendingBagClosureState
{
    Pending = 1,
    Completed = 2,
    PermanentlyFailed = 3
}

public sealed class PendingBagClosure
{
    private readonly Guid[] _parcelReadIds;

    private PendingBagClosure(
        Guid localOperationId,
        string deviceId,
        string destinationCenterCode,
        IEnumerable<Guid> parcelReadIds,
        string sealNumber,
        string transportType,
        DateTimeOffset createdAt,
        PendingBagClosureState state,
        int attempts,
        DateTimeOffset? nextAttemptAt,
        string? lastError,
        string? finalBagBarcode)
    {
        if (localOperationId == Guid.Empty) throw new DomainException("Local operation id is required.");
        if (string.IsNullOrWhiteSpace(deviceId)) throw new DomainException("Device id is required.");
        if (string.IsNullOrWhiteSpace(destinationCenterCode)) throw new DomainException("Destination is required.");
        if (string.IsNullOrWhiteSpace(sealNumber)) throw new DomainException("Seal number is required.");
        if (string.IsNullOrWhiteSpace(transportType)) throw new DomainException("Transport type is required.");

        _parcelReadIds = parcelReadIds.Distinct().ToArray();
        if (_parcelReadIds.Length == 0 || _parcelReadIds.Any(x => x == Guid.Empty))
        {
            throw new DomainException("A pending closure requires at least one parcel read.");
        }

        LocalOperationId = localOperationId;
        DeviceId = deviceId.Trim();
        DestinationCenterCode = destinationCenterCode.Trim();
        SealNumber = sealNumber.Trim();
        TransportType = transportType.Trim();
        CreatedAt = createdAt;
        State = state;
        Attempts = attempts;
        NextAttemptAt = nextAttemptAt;
        LastError = string.IsNullOrWhiteSpace(lastError) ? null : lastError.Trim();
        FinalBagBarcode = string.IsNullOrWhiteSpace(finalBagBarcode) ? null : finalBagBarcode.Trim();
    }

    public Guid LocalOperationId { get; }
    public string DeviceId { get; }
    public string DestinationCenterCode { get; }
    public IReadOnlyList<Guid> ParcelReadIds => _parcelReadIds;
    public string SealNumber { get; }
    public string TransportType { get; }
    public DateTimeOffset CreatedAt { get; }
    public PendingBagClosureState State { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }
    public string? FinalBagBarcode { get; private set; }

    public static PendingBagClosure Create(
        Guid localOperationId,
        string deviceId,
        PostalCenterCode destination,
        IEnumerable<Guid> parcelReadIds,
        string sealNumber,
        string transportType,
        DateTimeOffset createdAt) => new(
            localOperationId, deviceId, destination.Value, parcelReadIds, sealNumber, transportType,
            createdAt, PendingBagClosureState.Pending, 0, createdAt, null, null);

    public static PendingBagClosure Rehydrate(
        Guid localOperationId,
        string deviceId,
        string destinationCenterCode,
        IEnumerable<Guid> parcelReadIds,
        string sealNumber,
        string transportType,
        DateTimeOffset createdAt,
        PendingBagClosureState state,
        int attempts,
        DateTimeOffset? nextAttemptAt,
        string? lastError,
        string? finalBagBarcode) => new(
            localOperationId, deviceId, destinationCenterCode, parcelReadIds, sealNumber, transportType,
            createdAt, state, attempts, nextAttemptAt, lastError, finalBagBarcode);

    public void RecordFailedAttempt(string error, DateTimeOffset nextAttemptAt)
    {
        EnsurePending();
        if (string.IsNullOrWhiteSpace(error)) throw new DomainException("Failure description is required.");
        Attempts++;
        LastError = error.Trim();
        NextAttemptAt = nextAttemptAt;
    }

    public void Complete(string finalBagBarcode)
    {
        EnsurePending();
        if (string.IsNullOrWhiteSpace(finalBagBarcode)) throw new DomainException("Final bag barcode is required.");
        FinalBagBarcode = finalBagBarcode.Trim();
        State = PendingBagClosureState.Completed;
        NextAttemptAt = null;
    }

    public void MarkPermanentlyFailed(string error)
    {
        EnsurePending();
        if (string.IsNullOrWhiteSpace(error)) throw new DomainException("Failure description is required.");
        LastError = error.Trim();
        State = PendingBagClosureState.PermanentlyFailed;
        NextAttemptAt = null;
    }

    private void EnsurePending()
    {
        if (State != PendingBagClosureState.Pending) throw new DomainException("Only a pending closure can be changed.");
    }
}
