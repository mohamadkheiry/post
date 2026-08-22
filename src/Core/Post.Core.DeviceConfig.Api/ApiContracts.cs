namespace Post.Core.DeviceConfig.Api;

public sealed record RegisterDeviceRequest(
    string Name,
    string DeviceType,
    string LogicalCode,
    string Owner,
    string ExchangeCenterCode,
    string? Description);

public sealed record UpdateDeviceRequest(
    string Name,
    string DeviceType,
    string Owner,
    string? Description);

public sealed record DeviceActivationRequest(bool Active);

public sealed record PublishConfigurationRequest(
    string ExchangeCenterCode,
    IReadOnlyCollection<string>? PostalCodes,
    long ShortReadingThresholdSeconds,
    long ReturnThresholdSeconds,
    decimal WeightToleranceKg,
    decimal DimensionToleranceCm,
    bool AutoDispatchEnabled = false,
    int? MaximumOpenBagMinutes = null);

public sealed record PagedResponse<T>(
    IReadOnlyCollection<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class DeviceConfigApiMarker;
