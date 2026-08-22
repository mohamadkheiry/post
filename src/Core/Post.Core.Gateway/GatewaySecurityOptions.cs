namespace Post.Core.Gateway;

/// <summary>Gateway security and throttling configuration.</summary>
public sealed class GatewaySecurityOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Auth";

    /// <summary>Enables JWT authentication. It can be disabled only in local/test environments.</summary>
    public bool AuthenticationEnabled { get; init; } = true;

    /// <summary>JWT issuer.</summary>
    public string Issuer { get; init; } = "post-core-identity";

    /// <summary>JWT audience.</summary>
    public string Audience { get; init; } = "post-platform";

    /// <summary>Symmetric signing key supplied only through a secret provider.</summary>
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>Whether authority metadata must use HTTPS.</summary>
    public bool RequireHttpsMetadata { get; init; } = true;

    /// <summary>Maximum JWT clock skew.</summary>
    public int ClockSkewSeconds { get; init; } = 30;

    /// <summary>Token bucket capacity per authenticated subject or source IP.</summary>
    public int RateLimitPermitLimit { get; init; } = 200;

    /// <summary>Tokens replenished in each period.</summary>
    public int RateLimitTokensPerPeriod { get; init; } = 100;

    /// <summary>Replenishment period in seconds.</summary>
    public int RateLimitReplenishmentSeconds { get; init; } = 1;

    /// <summary>Maximum queued requests per partition.</summary>
    public int RateLimitQueueLimit { get; init; } = 20;

    /// <summary>Maximum JSON control-plane request body accepted before proxying.</summary>
    public long JsonRequestBodyMaximumBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Enables trusted reverse-proxy forwarded header processing.</summary>
    public bool ForwardedHeadersEnabled { get; init; }

    /// <summary>Maximum number of trusted forwarding hops.</summary>
    public int ForwardedHeadersForwardLimit { get; init; } = 1;

    /// <summary>Requires X-Forwarded-For and X-Forwarded-Proto value-count symmetry.</summary>
    public bool ForwardedHeadersRequireHeaderSymmetry { get; init; } = true;

    /// <summary>Exact proxy IP addresses trusted to set forwarding headers.</summary>
    public string[] ForwardedHeadersKnownProxies { get; init; } = [];

    /// <summary>Proxy CIDR networks trusted to set forwarding headers.</summary>
    public string[] ForwardedHeadersKnownNetworks { get; init; } = [];
}
