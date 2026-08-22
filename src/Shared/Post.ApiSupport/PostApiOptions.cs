namespace Post.ApiSupport;

public sealed class PostApiOptions
{
    public const string SectionName = "Api";
    public const long MinimumJsonRequestBodyBytes = 8 * 1024;
    public const long MaximumJsonRequestBodyBytes = 16 * 1024 * 1024;

    public bool AuthenticationEnabled { get; init; } = true;
    public bool SwaggerEnabled { get; init; }
    public bool MetricsEnabled { get; init; } = true;
    public long JsonRequestBodyMaximumBytes { get; init; } = 2 * 1024 * 1024;
}

public sealed class PostJwtOptions
{
    public const string SectionName = "Auth";

    public string Issuer { get; init; } = "Post.Core.Identity";
    public string Audience { get; init; } = "Post.Platform";
    public string SigningKey { get; init; } = string.Empty;
    public bool RequireHttpsMetadata { get; init; } = true;
    public int ClockSkewSeconds { get; init; } = 30;
}

public static class PostAuthorizationPolicies
{
    public const string Edge = "Edge";
    public const string Administrator = "Administrator";
}
