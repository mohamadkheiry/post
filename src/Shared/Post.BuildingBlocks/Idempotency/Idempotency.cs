namespace Post.BuildingBlocks.Idempotency;

/// <summary>Normalized scope and key used to deduplicate an effectful operation.</summary>
public readonly record struct IdempotencyKey
{
    private const int MaximumPartLength = 200;

    private IdempotencyKey(string scope, string value)
    {
        Scope = scope;
        Value = value;
    }

    /// <summary>Logical operation scope, such as bag-close.</summary>
    public string Scope { get; }

    /// <summary>Caller-issued stable value.</summary>
    public string Value { get; }

    /// <summary>Creates and validates an idempotency key.</summary>
    public static IdempotencyKey Create(string scope, string value)
    {
        var normalizedScope = Normalize(scope, nameof(scope));
        var normalizedValue = Normalize(value, nameof(value));
        return new IdempotencyKey(normalizedScope, normalizedValue);
    }

    /// <summary>Attempts to create an idempotency key without throwing.</summary>
    public static bool TryCreate(string? scope, string? value, out IdempotencyKey key)
    {
        key = default;
        if (!IsValidPart(scope) || !IsValidPart(value))
        {
            return false;
        }

        key = new IdempotencyKey(scope!.Trim(), value!.Trim());
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Scope}:{Value}";

    private static string Normalize(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > MaximumPartLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Idempotency key parts must be at most 200 characters and contain no control characters.", parameterName);
        }

        return normalized;
    }

    private static bool IsValidPart(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= MaximumPartLength &&
        !value.Any(char.IsControl);
}

/// <summary>Lifecycle of one idempotent operation.</summary>
public enum IdempotencyState
{
    /// <summary>One caller owns and is processing the key.</summary>
    Processing,
    /// <summary>A response was durably recorded.</summary>
    Completed
}

/// <summary>Durable representation returned by an idempotency store.</summary>
/// <param name="Key">Normalized key.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="RequestHash">Hash used to detect key reuse with a different payload.</param>
/// <param name="ResponsePayload">Serialized safe response for completed operations.</param>
/// <param name="CreatedAtUtc">Creation time.</param>
/// <param name="ExpiresAtUtc">Expiry time.</param>
public sealed record IdempotencyEntry(
    IdempotencyKey Key,
    IdempotencyState State,
    string RequestHash,
    string? ResponsePayload,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>Outcome of trying to acquire an idempotency key.</summary>
public enum IdempotencyAcquireStatus
{
    /// <summary>The caller acquired a new key.</summary>
    Acquired,
    /// <summary>The same key is already processing.</summary>
    InProgress,
    /// <summary>A completed response can be replayed.</summary>
    Completed,
    /// <summary>The key was reused with a different request hash.</summary>
    PayloadConflict
}

/// <summary>Result of acquiring an idempotency key.</summary>
/// <param name="Status">Acquire outcome.</param>
/// <param name="Entry">Existing or newly created entry.</param>
public sealed record IdempotencyAcquireResult(IdempotencyAcquireStatus Status, IdempotencyEntry Entry);

/// <summary>Persistence port for atomic idempotency acquisition and completion.</summary>
public interface IIdempotencyStore
{
    /// <summary>Atomically acquires or returns the existing key.</summary>
    ValueTask<IdempotencyAcquireResult> TryAcquireAsync(
        IdempotencyKey key,
        string requestHash,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Marks an acquired key complete with a replayable response.</summary>
    ValueTask CompleteAsync(
        IdempotencyKey key,
        string responsePayload,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a processing key when no side effect was committed.</summary>
    ValueTask ReleaseAsync(IdempotencyKey key, CancellationToken cancellationToken = default);
}
