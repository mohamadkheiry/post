using Post.Core.Domain.Messaging;
using Post.Core.Domain.Common;
using StackExchange.Redis;

namespace Post.Core.Caching;

public enum IdempotencyCacheScope
{
    ParcelLifecycle = 0,
    OperationalResult = 1
}

public sealed record IdempotencyCacheSnapshot(
    int SchemaVersion,
    string RequestHash,
    string ResponseSnapshot,
    IdempotencyStatus Status,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;

    public static IdempotencyCacheSnapshot FromRecord(IdempotencyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new IdempotencyCacheSnapshot(
            CurrentSchemaVersion,
            record.RequestHash,
            record.ResponseSnapshot,
            record.Status,
            record.UpdatedAt);
    }

    public bool Matches(IdempotencyRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return SchemaVersion == CurrentSchemaVersion &&
               string.Equals(RequestHash, record.RequestHash, StringComparison.Ordinal) &&
               string.Equals(ResponseSnapshot, record.ResponseSnapshot, StringComparison.Ordinal) &&
               Status == record.Status &&
               UpdatedAt == record.UpdatedAt;
    }

    public bool IsStructurallyValid() =>
        SchemaVersion == CurrentSchemaVersion &&
        !string.IsNullOrWhiteSpace(RequestHash) &&
        RequestHash.Length <= 128 &&
        ResponseSnapshot is not null &&
        ResponseSnapshot.Length <= 1_000_000 &&
        Enum.IsDefined(Status);
}

public sealed record RedisIdempotencyCacheOptions
{
    public bool Enabled { get; init; }

    public string? ConnectionString { get; init; }

    public string InstancePrefix { get; init; } = "post:core";

    public TimeSpan EntryTtl { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(5);

    public int CleanupBatchSize { get; init; } = 500;

    public bool RequireTls { get; init; }

    public bool RequireAuthentication { get; init; }

    public RedisIdempotencyCacheOptions Validate()
    {
        if (!Enabled)
        {
            return this;
        }

        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException(
                "Redis idempotency caching is enabled but Redis:ConnectionString is missing.");
        }

        if (string.IsNullOrWhiteSpace(InstancePrefix) ||
            InstancePrefix.Length > 64 ||
            InstancePrefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ':' and not '-' and not '_'))
        {
            throw new InvalidOperationException(
                "Redis:InstancePrefix must contain 1-64 ASCII letters, digits, colon, dash, or underscore characters.");
        }

        if (EntryTtl < TimeSpan.FromMinutes(1) || EntryTtl > TimeSpan.FromDays(30) ||
            CleanupInterval < TimeSpan.FromSeconds(10) || CleanupInterval > TimeSpan.FromHours(24) ||
            CleanupBatchSize is < 1 or > 10_000)
        {
            throw new InvalidOperationException("Redis idempotency TTL/cleanup settings are outside their safe ranges.");
        }

        ConfigurationOptions parsed;
        try
        {
            parsed = ConfigurationOptions.Parse(ConnectionString, true);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Redis:ConnectionString is invalid.");
        }

        if (parsed.EndPoints.Count == 0)
        {
            throw new InvalidOperationException("Redis:ConnectionString must contain at least one endpoint.");
        }

        if (RequireTls && !parsed.Ssl)
        {
            throw new InvalidOperationException("Redis transport must use TLS in this environment.");
        }

        if (RequireAuthentication &&
            (string.IsNullOrWhiteSpace(parsed.User) || string.IsNullOrWhiteSpace(parsed.Password)))
        {
            throw new InvalidOperationException(
                "Redis ACL authentication requires an explicit username and password in this environment.");
        }

        return this;
    }
}
