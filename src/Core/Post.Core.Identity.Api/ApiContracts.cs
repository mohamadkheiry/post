namespace Post.Core.Identity.Api;

internal sealed record IdentityAuthenticationRateLimitOptions(
    int PermitLimit,
    TimeSpan Window,
    int QueueLimit);

public sealed record AdminTokenRequest(
    string UserName,
    string? Password,
    int? TokenLifetimeSeconds = null);

public sealed record EdgeTokenRequest(
    string LogicalDeviceCode,
    string? DeviceSecret,
    int? TokenLifetimeSeconds = null);

public sealed record ProvisionEdgeRequest(
    Guid DeviceId,
    string LogicalDeviceCode,
    string ExchangeCenterCode);

public sealed record ProvisionedEdgeResult(
    Guid DeviceId,
    string? DeviceSecret,
    bool IsReplay,
    bool RecoveryRequired);

public sealed class IdentityApiMarker;
