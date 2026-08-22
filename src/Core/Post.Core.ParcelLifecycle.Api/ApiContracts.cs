using Post.Core.Application.Contracts;

namespace Post.Core.ParcelLifecycle.Api;

public sealed record InboundQueryRequest(
    string? IdempotencyKey,
    IReadOnlyCollection<string>? Barcodes,
    string ParcelType,
    string ServiceType,
    string OriginCenterCode,
    string DestinationCenterCode,
    Guid DeviceId,
    string ExchangeCenterCode,
    PhysicalAttributesDto? PhysicalAttributes,
    DateTimeOffset EdgeEventTime,
    string? CorrelationId);

public sealed record BagRequest(
    string BagBarcode,
    string OriginCenterCode,
    string DestinationCenterCode,
    Guid CreatedByDeviceId,
    IReadOnlyCollection<string>? MemberBarcodes,
    string SealNumber,
    string TransportType,
    DateTimeOffset ClosedAt,
    string? IdempotencyKey,
    string? CorrelationId);

public sealed record DispatchRequest(
    string DispatchId,
    string DestinationCenterCode,
    IReadOnlyCollection<string>? BagBarcodes,
    DateTimeOffset ClosedAt,
    string? IdempotencyKey,
    string? CorrelationId);

public sealed record PagedResponse<T>(
    IReadOnlyCollection<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class ParcelLifecycleApiMarker;
