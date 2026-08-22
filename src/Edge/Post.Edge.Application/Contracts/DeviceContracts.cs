namespace Post.Edge.Application.Contracts;

public static class DeviceAuthenticationLimits
{
    public const int MaximumDeviceIdCharacters = 128;
    public const int MaximumDeviceTokenCharacters = 512;
}

public sealed record DeviceConnectedRequest(string DeviceId, string RemoteIpAddress, string ConnectionId);

public sealed record DeviceAuthRequest(
    string DeviceId,
    string Token,
    string RemoteIpAddress,
    string ConnectionId);

public sealed record DeviceAuthResponse(
    int Status,
    Guid? SessionId,
    DateTimeOffset EdgeServerTime,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == 1;
}

public sealed record DeviceDisconnectedRequest(string DeviceId, string ConnectionId);

public sealed record DeviceConfigurationDto(
    string DeviceId,
    string LogicalId,
    bool IsActive,
    string SecretAlgorithm,
    int SecretIterations,
    string SecretSalt,
    string SecretHash,
    string? PreviousSecretAlgorithm = null,
    int? PreviousSecretIterations = null,
    string? PreviousSecretSalt = null,
    string? PreviousSecretHash = null,
    DateTimeOffset? PreviousSecretValidUntil = null);

public sealed record EdgeConfigurationDto(
    string CenterCode,
    long Version,
    TimeSpan MinRereadWindow,
    TimeSpan MaxRereadWindow,
    IReadOnlyCollection<DeviceConfigurationDto> Devices,
    IReadOnlyCollection<string> SupportedPostalCodes,
    bool AutoDispatchEnabled = false,
    int MaximumOpenBagMinutes = 60);

public sealed record ReplaceDeviceNetworkBindingsCommand(
    string DeviceId,
    IReadOnlyCollection<string> IpAddresses,
    string Actor,
    string CorrelationId);

public sealed record ReplaceDeviceNetworkBindingsResult(
    string DeviceId,
    IReadOnlyCollection<string> IpAddresses);

public static class LocalAdministratorInputLimits
{
    public const int MaximumUserNameCharacters = 128;
    public const int MinimumPasswordCharacters = 12;
    public const int MaximumPasswordCharacters = 512;
    public const int MaximumTokenCharacters = 256;
}

public sealed record LocalAdministratorLoginCommand(string UserName, string Password);
public sealed record LocalAdministratorLoginResult(string AccessToken, DateTimeOffset ExpiresAt);
public sealed record RotateLocalAdministratorPasswordCommand(
    string UserName,
    string CurrentPassword,
    string NewPassword);
