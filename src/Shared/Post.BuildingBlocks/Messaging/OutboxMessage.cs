using Post.BuildingBlocks.Results;

namespace Post.BuildingBlocks.Messaging;

/// <summary>Persistence-neutral lifecycle of an outbox message.</summary>
public enum OutboxMessageState
{
    /// <summary>Waiting for its next attempt.</summary>
    Pending,
    /// <summary>Claimed by one publisher.</summary>
    Processing,
    /// <summary>Durably acknowledged by the destination.</summary>
    Published,
    /// <summary>Stopped after exhausting retries or manual intervention.</summary>
    DeadLetter
}

/// <summary>
/// A persistence-neutral transactional outbox entity with token-checked state transitions.
/// Persistence adapters are responsible for optimistic concurrency around these mutations.
/// </summary>
public sealed class OutboxMessage
{
    private const int MaximumLastErrorLength = 2_048;

    private OutboxMessage(
        Guid id,
        Guid? operationId,
        string type,
        string schemaVersion,
        string destination,
        string payload,
        string correlationId,
        long sequence,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset nextAttemptAtUtc)
    {
        Id = id;
        OperationId = operationId;
        Type = type;
        SchemaVersion = schemaVersion;
        Destination = destination;
        Payload = payload;
        CorrelationId = correlationId;
        Sequence = sequence;
        OccurredAtUtc = occurredAtUtc;
        NextAttemptAtUtc = nextAttemptAtUtc;
        State = OutboxMessageState.Pending;
    }

    /// <summary>Unique message/idempotency identifier.</summary>
    public Guid Id { get; }
    /// <summary>Optional stable business operation identifier.</summary>
    public Guid? OperationId { get; }
    /// <summary>Stable message type.</summary>
    public string Type { get; }
    /// <summary>Payload schema version.</summary>
    public string SchemaVersion { get; }
    /// <summary>Logical destination.</summary>
    public string Destination { get; }
    /// <summary>Serialized payload.</summary>
    public string Payload { get; }
    /// <summary>End-to-end correlation identifier.</summary>
    public string CorrelationId { get; }
    /// <summary>Ordering sequence within its producer stream.</summary>
    public long Sequence { get; }
    /// <summary>UTC business occurrence time.</summary>
    public DateTimeOffset OccurredAtUtc { get; }
    /// <summary>Current lifecycle state.</summary>
    public OutboxMessageState State { get; private set; }
    /// <summary>Number of claims made so far.</summary>
    public int AttemptCount { get; private set; }
    /// <summary>Earliest next claim time.</summary>
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    /// <summary>Current claim token.</summary>
    public Guid? LockToken { get; private set; }
    /// <summary>Claim expiry time.</summary>
    public DateTimeOffset? LockedUntilUtc { get; private set; }
    /// <summary>Successful publication time.</summary>
    public DateTimeOffset? PublishedAtUtc { get; private set; }
    /// <summary>Last safe, redacted failure.</summary>
    public string? LastError { get; private set; }

    /// <summary>Creates a pending message while validating durable invariants.</summary>
    public static OutboxMessage Create(
        Guid id,
        string type,
        string schemaVersion,
        string destination,
        string payload,
        string correlationId,
        long sequence,
        DateTimeOffset occurredAtUtc,
        Guid? operationId = null,
        DateTimeOffset? firstAttemptAtUtc = null)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation id cannot be empty.", nameof(operationId));
        }

        var occurredUtc = occurredAtUtc.ToUniversalTime();
        var firstAttemptUtc = (firstAttemptAtUtc ?? occurredUtc).ToUniversalTime();
        return new OutboxMessage(
            id,
            operationId,
            type.Trim(),
            schemaVersion.Trim(),
            destination.Trim(),
            payload,
            correlationId.Trim(),
            sequence,
            occurredUtc,
            firstAttemptUtc);
    }

    /// <summary>Whether the message can be claimed at the supplied time.</summary>
    public bool IsDue(DateTimeOffset nowUtc) =>
        State is OutboxMessageState.Pending && NextAttemptAtUtc <= nowUtc.ToUniversalTime();

    /// <summary>Claims a due pending message and returns a token required for every follow-up transition.</summary>
    public Result<Guid> TryClaim(DateTimeOffset nowUtc, TimeSpan lockDuration)
    {
        if (lockDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lockDuration), "Lock duration must be positive.");
        }

        var now = nowUtc.ToUniversalTime();
        if (!IsDue(now))
        {
            return Result.Failure<Guid>(Error.Conflict("outbox.not_due", "The message is not due or is not pending."));
        }

        State = OutboxMessageState.Processing;
        AttemptCount++;
        LockToken = Guid.NewGuid();
        LockedUntilUtc = now.Add(lockDuration);
        return Result.Success(LockToken.Value);
    }

    /// <summary>Marks a claimed message published.</summary>
    public Result MarkPublished(Guid lockToken, DateTimeOffset publishedAtUtc)
    {
        var validation = ValidateClaim(lockToken);
        if (validation.IsFailure)
        {
            return validation;
        }

        State = OutboxMessageState.Published;
        PublishedAtUtc = publishedAtUtc.ToUniversalTime();
        ClearLock();
        LastError = null;
        return Result.Success();
    }

    /// <summary>Schedules retry or moves the message to dead letter when attempts are exhausted.</summary>
    public Result MarkFailed(
        Guid lockToken,
        DateTimeOffset failedAtUtc,
        string safeError,
        int maximumAttempts,
        IRetryBackoffPolicy backoffPolicy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeError);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAttempts);
        ArgumentNullException.ThrowIfNull(backoffPolicy);

        var validation = ValidateClaim(lockToken);
        if (validation.IsFailure)
        {
            return validation;
        }

        LastError = safeError.Length <= MaximumLastErrorLength
            ? safeError
            : safeError[..MaximumLastErrorLength];
        ClearLock();

        if (AttemptCount >= maximumAttempts)
        {
            State = OutboxMessageState.DeadLetter;
            return Result.Success();
        }

        State = OutboxMessageState.Pending;
        NextAttemptAtUtc = failedAtUtc.ToUniversalTime().Add(backoffPolicy.GetDelay(AttemptCount, Id));
        return Result.Success();
    }

    /// <summary>Returns an expired processing claim to pending state.</summary>
    public Result RecoverExpiredClaim(DateTimeOffset nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        if (State is not OutboxMessageState.Processing || LockedUntilUtc is null || LockedUntilUtc > now)
        {
            return Result.Failure(Error.Conflict("outbox.claim_not_expired", "No expired processing claim exists."));
        }

        State = OutboxMessageState.Pending;
        NextAttemptAtUtc = now;
        ClearLock();
        return Result.Success();
    }

    /// <summary>Moves a non-published message to dead letter for manual intervention.</summary>
    public Result MoveToDeadLetter(string safeReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        if (State is OutboxMessageState.Published)
        {
            return Result.Failure(Error.Conflict("outbox.already_published", "A published message cannot move to dead letter."));
        }

        State = OutboxMessageState.DeadLetter;
        LastError = safeReason.Length <= MaximumLastErrorLength
            ? safeReason
            : safeReason[..MaximumLastErrorLength];
        ClearLock();
        return Result.Success();
    }

    private Result ValidateClaim(Guid lockToken)
    {
        if (lockToken == Guid.Empty || State is not OutboxMessageState.Processing || LockToken != lockToken)
        {
            return Result.Failure(Error.Conflict("outbox.invalid_claim", "The outbox claim token is invalid."));
        }

        return Result.Success();
    }

    private void ClearLock()
    {
        LockToken = null;
        LockedUntilUtc = null;
    }
}
