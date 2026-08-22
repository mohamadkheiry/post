using System.Net;
using Post.Edge.Domain.Common;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Domain.Devices;

public sealed record DeviceReplica
{
    public DeviceReplica(
        string deviceId,
        string logicalCode,
        DeviceSecretVerifier secretVerifier,
        bool active,
        PostalCenterCode exchangeCenter,
        DeviceSecretVerifier? previousSecretVerifier = null,
        DateTimeOffset? previousSecretValidUntil = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new DomainException("Device id is required.");
        if (string.IsNullOrWhiteSpace(logicalCode)) throw new DomainException("Logical code is required.");
        ArgumentNullException.ThrowIfNull(secretVerifier);
        DeviceId = deviceId.Trim();
        LogicalCode = logicalCode.Trim();
        SecretVerifier = secretVerifier;
        if ((previousSecretVerifier is null) != (previousSecretValidUntil is null))
            throw new DomainException("Previous device verifier and grace expiry must be supplied together.");
        PreviousSecretVerifier = previousSecretVerifier;
        PreviousSecretValidUntil = previousSecretValidUntil;
        Active = active;
        ExchangeCenterCode = exchangeCenter.Value;
    }

    public string DeviceId { get; }
    public string LogicalCode { get; }
    public DeviceSecretVerifier SecretVerifier { get; }
    public DeviceSecretVerifier? PreviousSecretVerifier { get; }
    public DateTimeOffset? PreviousSecretValidUntil { get; }
    public bool Active { get; }
    public string ExchangeCenterCode { get; }
}

public sealed record DeviceNetworkBinding
{
    public DeviceNetworkBinding(string deviceId, string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new DomainException("Device id is required.");
        if (!IPAddress.TryParse(ipAddress, out var parsed)) throw new DomainException("A valid IP address is required.");
        DeviceId = deviceId.Trim();
        IpAddress = parsed.MapToIPv6().ToString();
    }

    public string DeviceId { get; }
    public string IpAddress { get; }

    public bool Matches(string remoteIp) => IPAddress.TryParse(remoteIp, out var parsed) &&
                                            string.Equals(IpAddress, parsed.MapToIPv6().ToString(), StringComparison.OrdinalIgnoreCase);
}

public enum DeviceSessionState
{
    Connected = 1,
    Authenticated = 2,
    Disconnected = 3
}

public sealed class DeviceSession
{
    private DeviceSession(Guid sessionId, string connectionId, string sourceIp, DateTimeOffset connectedAt)
    {
        if (sessionId == Guid.Empty) throw new DomainException("Session id is required.");
        if (string.IsNullOrWhiteSpace(connectionId)) throw new DomainException("Connection id is required.");
        if (!IPAddress.TryParse(sourceIp, out var parsed)) throw new DomainException("A valid source IP is required.");
        SessionId = sessionId;
        ConnectionId = connectionId.Trim();
        SourceIp = parsed.MapToIPv6().ToString();
        ConnectedAt = connectedAt;
        State = DeviceSessionState.Connected;
    }

    public Guid SessionId { get; }
    public string ConnectionId { get; }
    public string SourceIp { get; }
    public string? DeviceId { get; private set; }
    public DateTimeOffset ConnectedAt { get; }
    public DateTimeOffset? DisconnectedAt { get; private set; }
    public DeviceSessionState State { get; private set; }

    public static DeviceSession Connect(Guid sessionId, string connectionId, string sourceIp, DateTimeOffset connectedAt)
        => new(sessionId, connectionId, sourceIp, connectedAt);

    public void Authenticate(string deviceId)
    {
        if (State != DeviceSessionState.Connected) throw new DomainException("Only a connected session can be authenticated.");
        if (string.IsNullOrWhiteSpace(deviceId)) throw new DomainException("Device id is required.");
        DeviceId = deviceId.Trim();
        State = DeviceSessionState.Authenticated;
    }

    public void Disconnect(DateTimeOffset disconnectedAt)
    {
        if (State == DeviceSessionState.Disconnected) return;
        if (disconnectedAt < ConnectedAt) throw new DomainException("Disconnect time cannot precede connect time.");
        DisconnectedAt = disconnectedAt;
        State = DeviceSessionState.Disconnected;
    }
}

public sealed record ConfigurationSnapshot
{
    public ConfigurationSnapshot(
        string exchangeCenterCode,
        long version,
        DateTimeOffset generatedAt,
        IEnumerable<DeviceReplica> devices,
        IEnumerable<string> postalCodes,
        TimeSpan shortRereadWindow,
        TimeSpan returnWindow,
        bool autoDispatchEnabled = false,
        int maximumOpenBagMinutes = 60)
    {
        if (version < 0) throw new DomainException("Configuration version cannot be negative.");
        if (shortRereadWindow <= TimeSpan.Zero || returnWindow <= shortRereadWindow)
            throw new DomainException("Reread windows are invalid.");
        if (maximumOpenBagMinutes is < 1 or > 10_080)
            throw new DomainException("Maximum open-bag duration is invalid.");
        ExchangeCenterCode = PostalCenterCode.Create(exchangeCenterCode).Value;
        Version = version;
        GeneratedAt = generatedAt;
        Devices = devices?.ToArray() ?? throw new ArgumentNullException(nameof(devices));
        PostalCodes = postalCodes?.Select(x => PostalCenterCode.Create(x).Value).Distinct().ToArray()
                      ?? throw new ArgumentNullException(nameof(postalCodes));
        ShortRereadWindow = shortRereadWindow;
        ReturnWindow = returnWindow;
        AutoDispatchEnabled = autoDispatchEnabled;
        MaximumOpenBagMinutes = maximumOpenBagMinutes;
    }

    public string ExchangeCenterCode { get; }
    public long Version { get; }
    public DateTimeOffset GeneratedAt { get; }
    public IReadOnlyList<DeviceReplica> Devices { get; }
    public IReadOnlyList<string> PostalCodes { get; }
    public TimeSpan ShortRereadWindow { get; }
    public TimeSpan ReturnWindow { get; }
    public bool AutoDispatchEnabled { get; }
    public int MaximumOpenBagMinutes { get; }
}
