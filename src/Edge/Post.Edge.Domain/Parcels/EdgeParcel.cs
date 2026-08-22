using Post.Edge.Domain.Common;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Domain.Parcels;

public enum EdgeParcelState
{
    Registered = 1,
    Pending = 2,
    DestinationAssigned = 3,
    ExportRegistered = 4,
    Deferred = 5,
    Bagged = 6,
    Unresolved = 7
}

public sealed class EdgeParcel
{
    private readonly List<ParcelError> _errors;
    private readonly string[] _aliases;

    private EdgeParcel(
        Guid readId,
        string canonicalBarcode,
        IEnumerable<string> aliases,
        string deviceId,
        DateTimeOffset readTimestamp,
        PhysicalAttributes physicalAttributes,
        string? parcelType,
        string? serviceType,
        EdgeParcelState state,
        string? operationalDestination,
        string? chuteId,
        Guid? previousReadId,
        string? bagBarcode,
        IEnumerable<ParcelError>? errors,
        bool coreSynced)
    {
        if (readId == Guid.Empty) throw new DomainException("Read id is required.");
        if (string.IsNullOrWhiteSpace(canonicalBarcode)) throw new DomainException("Canonical barcode is required.");
        if (string.IsNullOrWhiteSpace(deviceId)) throw new DomainException("Device id is required.");

        ReadId = readId;
        CanonicalBarcode = canonicalBarcode;
        _aliases = aliases.Distinct(StringComparer.Ordinal).ToArray();
        if (_aliases.Length == 0) throw new DomainException("At least one barcode alias is required.");
        DeviceId = deviceId.Trim();
        ReadTimestamp = readTimestamp;
        PhysicalAttributes = physicalAttributes;
        ParcelType = NormalizeOptional(parcelType);
        ServiceType = NormalizeOptional(serviceType);
        State = state;
        OperationalDestinationCode = NormalizeOptional(operationalDestination);
        ChuteId = NormalizeOptional(chuteId);
        PreviousReadId = previousReadId;
        BagBarcode = NormalizeOptional(bagBarcode);
        _errors = errors?.ToList() ?? [];
        CoreSynced = coreSynced;
    }

    public Guid ReadId { get; }
    public string CanonicalBarcode { get; }
    public IReadOnlyList<string> Aliases => _aliases;
    public string DeviceId { get; }
    public DateTimeOffset ReadTimestamp { get; }
    public PhysicalAttributes PhysicalAttributes { get; }
    public string? ParcelType { get; }
    public string? ServiceType { get; }
    public EdgeParcelState State { get; private set; }
    public string? OperationalDestinationCode { get; private set; }
    public string? ChuteId { get; private set; }
    public Guid? PreviousReadId { get; }
    public string? BagBarcode { get; private set; }
    public IReadOnlyList<ParcelError> Errors => _errors;
    public bool CoreSynced { get; private set; }
    public bool IsImmutable => State == EdgeParcelState.Bagged;

    public static EdgeParcel Create(
        Guid readId,
        BarcodeSet barcodes,
        string deviceId,
        DateTimeOffset readTimestamp,
        PhysicalAttributes? physicalAttributes = null,
        string? parcelType = null,
        string? serviceType = null,
        bool pending = false,
        Guid? previousReadId = null)
    {
        ArgumentNullException.ThrowIfNull(barcodes);
        return new EdgeParcel(
            readId,
            barcodes.CanonicalBarcode.Canonical,
            barcodes.Aliases,
            deviceId,
            readTimestamp,
            physicalAttributes ?? new PhysicalAttributes(null, null),
            parcelType,
            serviceType,
            pending ? EdgeParcelState.Pending : EdgeParcelState.Registered,
            null,
            null,
            previousReadId,
            null,
            null,
            false);
    }

    public static EdgeParcel Rehydrate(
        Guid readId,
        string canonicalBarcode,
        IEnumerable<string> aliases,
        string deviceId,
        DateTimeOffset readTimestamp,
        PhysicalAttributes physicalAttributes,
        string? parcelType,
        string? serviceType,
        EdgeParcelState state,
        string? operationalDestination,
        string? chuteId,
        Guid? previousReadId,
        string? bagBarcode,
        IEnumerable<ParcelError>? errors,
        bool coreSynced) => new(
            readId, canonicalBarcode, aliases, deviceId, readTimestamp, physicalAttributes,
            parcelType, serviceType, state, operationalDestination, chuteId, previousReadId,
            bagBarcode, errors, coreSynced);

    public void ResolvePending()
    {
        EnsureState(EdgeParcelState.Pending);
        State = EdgeParcelState.Registered;
    }

    public void FailPending(ParcelError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        EnsureState(EdgeParcelState.Pending);
        _errors.Add(error);
        State = EdgeParcelState.Unresolved;
    }

    public void RejectInboundDecision(ParcelError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (State is not (EdgeParcelState.Pending or EdgeParcelState.Registered))
            throw new DomainException("Only an unassigned inbound parcel can be rejected by Core.");
        _errors.Add(error);
        State = EdgeParcelState.Unresolved;
    }

    public void AssignDestination(PostalCenterCode destination, string? chuteId)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (State is not (EdgeParcelState.Registered or EdgeParcelState.DestinationAssigned))
        {
            throw new DomainException("Destination can only be assigned to a registered, unbagged parcel.");
        }

        var changed = !string.Equals(OperationalDestinationCode, destination.Value, StringComparison.Ordinal);
        OperationalDestinationCode = destination.Value;
        ChuteId = changed ? NormalizeOptional(chuteId) : NormalizeOptional(chuteId) ?? ChuteId;
        State = EdgeParcelState.DestinationAssigned;
    }

    public void MarkExportRegistered()
    {
        if (State is not (EdgeParcelState.DestinationAssigned or EdgeParcelState.Deferred))
        {
            throw new DomainException("Export can only be registered for a destination-assigned or deferred parcel.");
        }

        State = EdgeParcelState.ExportRegistered;
    }

    public void MarkDeferred(ParcelError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (State != EdgeParcelState.DestinationAssigned)
        {
            throw new DomainException("Only a destination-assigned parcel can be deferred.");
        }

        _errors.Add(error);
        State = EdgeParcelState.Deferred;
    }

    public void RecordDeferredFailure(ParcelError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (State != EdgeParcelState.Deferred)
        {
            throw new DomainException("Only a deferred parcel can receive another deferred failure.");
        }

        _errors.Add(error);
    }

    public void MarkBagged(string bagBarcode)
    {
        EnsureState(EdgeParcelState.ExportRegistered);
        if (string.IsNullOrWhiteSpace(bagBarcode)) throw new DomainException("Bag barcode is required.");
        BagBarcode = bagBarcode.Trim();
        State = EdgeParcelState.Bagged;
    }

    public void MarkUnresolved()
    {
        EnsureState(EdgeParcelState.Deferred);
        State = EdgeParcelState.Unresolved;
    }

    public void MarkCoreSynced() => CoreSynced = true;

    private void EnsureState(EdgeParcelState expected)
    {
        if (State != expected) throw new DomainException($"Operation requires parcel state {expected}; current state is {State}.");
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
