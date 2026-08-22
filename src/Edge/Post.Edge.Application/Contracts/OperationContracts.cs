using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;
using System.Text.Json;

namespace Post.Edge.Application.Contracts;

public sealed record OperationIdentity(Guid Value, bool IsBestEffortFallback)
{
    public static OperationIdentity Resolve(Guid? operationId, Guid correlationId)
    {
        if (operationId is { } stable && stable != Guid.Empty)
        {
            return new OperationIdentity(stable, false);
        }

        if (correlationId == Guid.Empty)
        {
            throw new ArgumentException("Either OperationId or a non-empty CorrelationId is required.");
        }

        return new OperationIdentity(correlationId, true);
    }
}

public enum InboundResultStatus
{
    Error = 0,
    Registered = 1,
    Pending = 2,
    Returned = 3,
    Refused = 4
}

public sealed record InboundImageDto(
    string ImageId,
    string ContentBase64,
    string? MimeType = null,
    string? Description = null);

public static class InboundEvidenceLimits
{
    public const int MaximumImageCount = 32;
    public const int MaximumImageBase64Characters = 5_000_000;
    public const int MaximumAggregateImageBase64Characters =
        MaximumImageCount * MaximumImageBase64Characters;
    public const int MaximumDecodedImageBytes = 3_750_000;
    public const int MaximumAggregateDecodedImageBytes =
        MaximumImageCount * MaximumDecodedImageBytes;
}

public sealed record InboundParcelRequest(
    Guid CorrelationId,
    Guid? OperationId,
    string DeviceId,
    IReadOnlyCollection<string> Barcodes,
    DateTimeOffset ReadTimestamp,
    decimal? Weight,
    decimal? Length,
    decimal? Width,
    decimal? Height,
    string? ParcelType,
    string? ServiceType,
    TimeSpan? PostalTimeout = null,
    JsonElement? SupplementaryData = null,
    IReadOnlyCollection<InboundImageDto>? Images = null,
    int? TimeoutMs = null);

public sealed record InboundParcelResponse(
    Guid CorrelationId,
    Guid OperationId,
    Guid? ReadId,
    InboundResultStatus Status,
    string CanonicalBarcode,
    string? OriginCode,
    string? DestinationCode,
    string? DiscrepancyCode = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool OperationIdIsBestEffort = false);

public sealed record DestinationAssignRequest(
    Guid CorrelationId,
    Guid? OperationId,
    string DeviceId,
    Guid? ReadId,
    string DestinationCode,
    string? Chute = null,
    string? Barcode = null);

public sealed record DestinationAssignResponse(
    Guid CorrelationId,
    Guid OperationId,
    int Status,
    Guid ReadId,
    string? DestinationCode = null,
    string? Chute = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool OperationIdIsBestEffort = false)
{
    public bool IsSuccess => Status == 1;
}

public sealed record ParcelSelectionFilter(
    string DeviceId,
    string DestinationCode,
    Guid? LastReadId,
    string? LastCanonicalBarcode,
    int? Count,
    DateTimeOffset? From,
    DateTimeOffset? To,
    IReadOnlyCollection<string>? ChuteIds = null,
    IReadOnlyCollection<string>? ParcelTypes = null,
    IReadOnlyCollection<string>? ServiceTypes = null);

public sealed record SelectedParcel(
    Guid ReadId,
    string CanonicalBarcode,
    DateTimeOffset ReadTimestamp,
    EdgeParcelState State,
    decimal? Weight);

public sealed record CloseBagRequest(
    Guid CorrelationId,
    Guid? OperationId,
    string DeviceId,
    string DestinationCode,
    Guid? LastReadId,
    string? LastCanonicalBarcode,
    int? Count,
    DateTimeOffset? From,
    DateTimeOffset? To,
    string SealNumber,
    string TransportType,
    IReadOnlyCollection<string>? ChuteIds = null,
    IReadOnlyCollection<string>? ParcelTypes = null,
    IReadOnlyCollection<string>? ServiceTypes = null);

public sealed record BagErrorDto(
    Guid ReadId,
    string CanonicalBarcode,
    string ErrorCode,
    string Description,
    string? Category);

public sealed record CloseBagResponse(
    Guid CorrelationId,
    Guid OperationId,
    BagCloseResult Result,
    string? BagBarcode,
    string? LabelPayload,
    BagCounts Counts,
    IReadOnlyCollection<BagErrorDto> Errors,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool OperationIdIsBestEffort = false);

public enum BagCloseResult
{
    Error = 0,
    Completed = 1,
    LabelPending = 2,
    NoEligibleParcel = 3,
    AllParcelsFailed = 4
}

public sealed record CloseDispatchRequest(
    Guid CorrelationId,
    Guid? OperationId,
    string DeviceId,
    string DestinationCode,
    IReadOnlyCollection<string>? BagBarcodes = null);

public sealed record CloseDispatchResponse(
    Guid CorrelationId,
    Guid OperationId,
    int Status,
    string? DispatchId,
    IReadOnlyCollection<string> BagBarcodes,
    int TotalParcels,
    IReadOnlyCollection<DispatchProblemDto> ProblemParcels,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool OperationIdIsBestEffort = false)
{
    public bool IsSuccess => Status == 1;
}

public sealed record DispatchProblemDto(
    string Barcode,
    string ErrorCode,
    string Description,
    string BagBarcode);

public sealed record PendingWorkResult(int Processed, int Completed, int Retried, int DeadLettered);
