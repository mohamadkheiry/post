using Post.Edge.Application.Contracts;

namespace Post.Edge.Api;

public sealed class EdgeTrustedProxyOptions
{
    public const string SectionName = "Edge:TrustedProxy";
    public bool Enabled { get; set; }
    public int ForwardLimit { get; set; } = 1;
    public bool RequireHeaderSymmetry { get; set; } = true;
    public List<string> KnownProxies { get; set; } = [];
    public List<string> KnownNetworks { get; set; } = [];
}

public sealed class EdgeDeviceTransportOptions
{
    public const string SectionName = "Edge:DeviceTransport";

    public long MaximumReceiveMessageSizeBytes { get; set; } = 192L * 1024 * 1024;
    public int ConnectionPermitLimit { get; set; } = 20;
    public TimeSpan ConnectionRateWindow { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan AuthenticationDeadline { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan FailedAuthenticationCloseDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan ClientTimeoutInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (MaximumReceiveMessageSizeBytes is < 1_048_576 or > 268_435_456)
            throw new InvalidOperationException("Edge device receive limit must be between 1 MiB and 256 MiB.");
        if ((long)InboundEvidenceLimits.MaximumAggregateImageBase64Characters + 1_048_576 >
            MaximumReceiveMessageSizeBytes)
            throw new InvalidOperationException("Edge receive limit must leave at least 1 MiB beyond the aggregate image limit.");
        if (ConnectionPermitLimit is < 1 or > 1_000)
            throw new InvalidOperationException("Edge connection permit limit must be between 1 and 1000.");
        if (ConnectionRateWindow < TimeSpan.FromSeconds(1) || ConnectionRateWindow > TimeSpan.FromHours(1))
            throw new InvalidOperationException("Edge connection rate window must be between 1 second and 1 hour.");
        if (AuthenticationDeadline < TimeSpan.FromSeconds(1) || AuthenticationDeadline > TimeSpan.FromMinutes(2))
            throw new InvalidOperationException("Edge authentication deadline must be between 1 second and 2 minutes.");
        if (FailedAuthenticationCloseDelay < TimeSpan.FromMilliseconds(25) ||
            FailedAuthenticationCloseDelay > TimeSpan.FromSeconds(2))
            throw new InvalidOperationException("Edge failed-auth close delay must be between 25 ms and 2 seconds.");
        if (HandshakeTimeout < TimeSpan.FromSeconds(5) || HandshakeTimeout > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Edge SignalR handshake timeout must be between 5 seconds and 1 minute.");
        if (KeepAliveInterval < TimeSpan.FromSeconds(1) || KeepAliveInterval > TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Edge SignalR keepalive interval must be between 1 second and 1 minute.");
        if (ClientTimeoutInterval < KeepAliveInterval * 2 || ClientTimeoutInterval > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException(
                "Edge SignalR client timeout must be at least twice keepalive and at most 5 minutes.");
    }
}
