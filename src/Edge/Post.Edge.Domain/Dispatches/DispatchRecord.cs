using Post.Edge.Domain.Common;

namespace Post.Edge.Domain.Dispatches;

public sealed class DispatchRecord
{
    private readonly string[] _bagBarcodes;

    private DispatchRecord(
        Guid localOperationId,
        string dispatchId,
        string deviceId,
        string destinationCenterCode,
        IEnumerable<string> bagBarcodes,
        DateTimeOffset createdAt,
        bool coreConfirmed)
    {
        if (localOperationId == Guid.Empty) throw new DomainException("Local operation id is required.");
        if (string.IsNullOrWhiteSpace(dispatchId)) throw new DomainException("Dispatch id is required.");
        if (string.IsNullOrWhiteSpace(deviceId)) throw new DomainException("Device id is required.");
        if (string.IsNullOrWhiteSpace(destinationCenterCode)) throw new DomainException("Destination is required.");
        _bagBarcodes = bagBarcodes.Distinct(StringComparer.Ordinal).ToArray();
        if (_bagBarcodes.Length == 0 || _bagBarcodes.Any(string.IsNullOrWhiteSpace))
        {
            throw new DomainException("A dispatch requires at least one valid bag barcode.");
        }
        LocalOperationId = localOperationId;
        DispatchId = dispatchId.Trim();
        DeviceId = deviceId.Trim();
        DestinationCenterCode = destinationCenterCode.Trim();
        CreatedAt = createdAt;
        CoreConfirmed = coreConfirmed;
    }

    public Guid LocalOperationId { get; }
    public string DispatchId { get; }
    public string DeviceId { get; }
    public string DestinationCenterCode { get; }
    public IReadOnlyList<string> BagBarcodes => _bagBarcodes;
    public DateTimeOffset CreatedAt { get; }
    public bool CoreConfirmed { get; private set; }

    public static DispatchRecord Create(
        Guid localOperationId,
        string dispatchId,
        string deviceId,
        string destinationCenterCode,
        IEnumerable<string> bagBarcodes,
        DateTimeOffset createdAt) => new(
            localOperationId, dispatchId, deviceId, destinationCenterCode, bagBarcodes, createdAt, false);

    public static DispatchRecord Rehydrate(
        Guid localOperationId,
        string dispatchId,
        string deviceId,
        string destinationCenterCode,
        IEnumerable<string> bagBarcodes,
        DateTimeOffset createdAt,
        bool coreConfirmed) => new(
            localOperationId, dispatchId, deviceId, destinationCenterCode, bagBarcodes, createdAt, coreConfirmed);

    public void MarkCoreConfirmed() => CoreConfirmed = true;
}
