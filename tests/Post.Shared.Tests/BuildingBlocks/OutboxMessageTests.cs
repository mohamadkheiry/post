using Post.BuildingBlocks.Messaging;
using System.Globalization;

namespace PostShared.Tests.BuildingBlocks;

public sealed class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-16T08:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void CreateNormalizesValuesAndInitializesPendingState()
    {
        var id = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var localTime = new DateTimeOffset(2026, 8, 16, 11, 30, 0, TimeSpan.FromHours(3.5));

        var message = OutboxMessage.Create(
            id,
            " type ",
            " 1.0 ",
            " core ",
            "{}",
            " correlation ",
            7,
            localTime,
            operationId,
            localTime.AddMinutes(1));

        Assert.Equal(id, message.Id);
        Assert.Equal(operationId, message.OperationId);
        Assert.Equal("type", message.Type);
        Assert.Equal("1.0", message.SchemaVersion);
        Assert.Equal("core", message.Destination);
        Assert.Equal("correlation", message.CorrelationId);
        Assert.Equal("{}", message.Payload);
        Assert.Equal(7, message.Sequence);
        Assert.Equal(TimeSpan.Zero, message.OccurredAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, message.NextAttemptAtUtc.Offset);
        Assert.Equal(OutboxMessageState.Pending, message.State);
        Assert.Equal(0, message.AttemptCount);
        Assert.Null(message.LockToken);
        Assert.Null(message.PublishedAtUtc);
        Assert.Null(message.LastError);
    }

    [Fact]
    public void CreateDefaultsFirstAttemptToOccurrence()
    {
        var message = CreateMessage();
        Assert.Equal(message.OccurredAtUtc, message.NextAttemptAtUtc);
    }

    [Fact]
    public void CreateRejectsInvalidInvariants()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateWith(id: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateWith(type: " "));
        Assert.Throws<ArgumentException>(() => CreateWith(schema: " "));
        Assert.Throws<ArgumentException>(() => CreateWith(destination: " "));
        Assert.Throws<ArgumentException>(() => CreateWith(payload: " "));
        Assert.Throws<ArgumentException>(() => CreateWith(correlation: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateWith(sequence: -1));
        Assert.Throws<ArgumentException>(() => CreateWith(operationId: Guid.Empty));
    }

    [Fact]
    public void IsDueRequiresPendingStateAndDueTime()
    {
        var message = CreateMessage(firstAttemptAt: Now.AddMinutes(1));

        Assert.False(message.IsDue(Now));
        Assert.True(message.IsDue(Now.AddMinutes(1)));

        var claim = message.TryClaim(Now.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.True(claim.IsSuccess);
        Assert.False(message.IsDue(Now.AddMinutes(2)));
    }

    [Fact]
    public void TryClaimValidatesLockDurationAndDueState()
    {
        var notDue = CreateMessage(firstAttemptAt: Now.AddMinutes(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => notDue.TryClaim(Now, TimeSpan.Zero));

        var result = notDue.TryClaim(Now, TimeSpan.FromSeconds(30));

        Assert.True(result.IsFailure);
        Assert.Equal("outbox.not_due", result.Error.Code);
    }

    [Fact]
    public void TryClaimSetsProcessingAttemptTokenAndExpiry()
    {
        var message = CreateMessage();

        var result = message.TryClaim(Now, TimeSpan.FromSeconds(30));

        Assert.True(result.IsSuccess);
        Assert.NotEqual(Guid.Empty, result.Value);
        Assert.Equal(result.Value, message.LockToken);
        Assert.Equal(OutboxMessageState.Processing, message.State);
        Assert.Equal(1, message.AttemptCount);
        Assert.Equal(Now.AddSeconds(30), message.LockedUntilUtc);
        Assert.True(message.TryClaim(Now, TimeSpan.FromSeconds(30)).IsFailure);
    }

    [Fact]
    public void MarkPublishedRequiresMatchingClaimAndCompletesMessage()
    {
        var message = CreateMessage();
        var token = message.TryClaim(Now, TimeSpan.FromMinutes(1)).Value;

        Assert.True(message.MarkPublished(Guid.NewGuid(), Now).IsFailure);
        Assert.True(message.MarkPublished(Guid.Empty, Now).IsFailure);

        var result = message.MarkPublished(token, Now.AddSeconds(1));

        Assert.True(result.IsSuccess);
        Assert.Equal(OutboxMessageState.Published, message.State);
        Assert.Equal(Now.AddSeconds(1), message.PublishedAtUtc);
        Assert.Null(message.LockToken);
        Assert.Null(message.LockedUntilUtc);
        Assert.Null(message.LastError);
        Assert.True(message.MarkPublished(token, Now.AddSeconds(2)).IsFailure);
    }

    [Fact]
    public void MarkFailedValidatesInputsAndClaimToken()
    {
        var message = CreateMessage();
        var token = message.TryClaim(Now, TimeSpan.FromMinutes(1)).Value;
        var policy = CreateBackoff();

        Assert.Throws<ArgumentException>(() => message.MarkFailed(token, Now, " ", 3, policy));
        Assert.Throws<ArgumentOutOfRangeException>(() => message.MarkFailed(token, Now, "safe", 0, policy));
        Assert.Throws<ArgumentNullException>(() => message.MarkFailed(token, Now, "safe", 3, null!));
        Assert.True(message.MarkFailed(Guid.NewGuid(), Now, "safe", 3, policy).IsFailure);
    }

    [Fact]
    public void MarkFailedSchedulesBackoffAndCanBeReclaimed()
    {
        var message = CreateMessage();
        var token = message.TryClaim(Now, TimeSpan.FromMinutes(1)).Value;

        var failed = message.MarkFailed(token, Now, "safe error", 3, CreateBackoff());

        Assert.True(failed.IsSuccess);
        Assert.Equal(OutboxMessageState.Pending, message.State);
        Assert.Equal(Now.AddSeconds(10), message.NextAttemptAtUtc);
        Assert.Equal("safe error", message.LastError);
        Assert.Null(message.LockToken);
        Assert.False(message.IsDue(Now.AddSeconds(9)));
        Assert.True(message.TryClaim(Now.AddSeconds(10), TimeSpan.FromMinutes(1)).IsSuccess);
        Assert.Equal(2, message.AttemptCount);
    }

    [Fact]
    public void MarkFailedDeadLettersAtMaximumAttemptsAndTruncatesError()
    {
        var message = CreateMessage();
        var token = message.TryClaim(Now, TimeSpan.FromMinutes(1)).Value;
        var longError = new string('x', 3_000);

        var failed = message.MarkFailed(token, Now, longError, 1, CreateBackoff());

        Assert.True(failed.IsSuccess);
        Assert.Equal(OutboxMessageState.DeadLetter, message.State);
        Assert.Equal(2_048, message.LastError!.Length);
        Assert.Null(message.LockToken);
    }

    [Fact]
    public void RecoverExpiredClaimRequiresExpiredProcessingClaim()
    {
        var pending = CreateMessage();
        Assert.True(pending.RecoverExpiredClaim(Now).IsFailure);

        var processing = CreateMessage();
        processing.TryClaim(Now, TimeSpan.FromMinutes(1));
        Assert.True(processing.RecoverExpiredClaim(Now.AddSeconds(59)).IsFailure);

        var recovered = processing.RecoverExpiredClaim(Now.AddMinutes(1));

        Assert.True(recovered.IsSuccess);
        Assert.Equal(OutboxMessageState.Pending, processing.State);
        Assert.Equal(Now.AddMinutes(1), processing.NextAttemptAtUtc);
        Assert.Null(processing.LockToken);
        Assert.Null(processing.LockedUntilUtc);
    }

    [Fact]
    public void MoveToDeadLetterValidatesReasonClearsClaimAndRejectsPublishedMessage()
    {
        var processing = CreateMessage();
        processing.TryClaim(Now, TimeSpan.FromMinutes(1));

        Assert.Throws<ArgumentException>(() => processing.MoveToDeadLetter(" "));
        Assert.True(processing.MoveToDeadLetter("manual").IsSuccess);
        Assert.Equal(OutboxMessageState.DeadLetter, processing.State);
        Assert.Equal("manual", processing.LastError);
        Assert.Null(processing.LockToken);

        var published = CreateMessage();
        var token = published.TryClaim(Now, TimeSpan.FromMinutes(1)).Value;
        published.MarkPublished(token, Now);
        Assert.True(published.MoveToDeadLetter("manual").IsFailure);
        Assert.Equal(OutboxMessageState.Published, published.State);
    }

    private static ExponentialBackoffPolicy CreateBackoff() => new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromMinutes(1),
        multiplier: 2,
        jitterRatio: 0);

    private static OutboxMessage CreateMessage(DateTimeOffset? firstAttemptAt = null) => CreateWith(firstAttemptAt: firstAttemptAt);

    private static OutboxMessage CreateWith(
        Guid? id = null,
        string type = "parcel.read.v1",
        string schema = "1.0",
        string destination = "core",
        string payload = "{}",
        string correlation = "corr",
        long sequence = 0,
        Guid? operationId = null,
        DateTimeOffset? firstAttemptAt = null) => OutboxMessage.Create(
            id ?? Guid.NewGuid(),
            type,
            schema,
            destination,
            payload,
            correlation,
            sequence,
            Now,
            operationId,
            firstAttemptAt);
}
