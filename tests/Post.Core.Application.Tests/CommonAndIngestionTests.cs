using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Application.Parcels;
using Post.Core.Domain.Common;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Application.Tests;

public sealed class CommonAndIngestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CanonicalHasherCorrelationAndIdempotencyApplyStableRules()
    {
        var hasher = new CanonicalJsonRequestHasher();
        Assert.Equal(hasher.Hash(new { Value = 1 }), hasher.Hash(new { Value = 1 }));
        Assert.NotEqual(hasher.Hash(new { Value = 1 }), hasher.Hash(new { Value = 2 }));

        var correlation = new CorrelationService();
        Assert.Equal("supplied", correlation.GetOrCreate(" supplied "));
        Assert.False(string.IsNullOrWhiteSpace(correlation.GetOrCreate(null)));

        var service = new IdempotencyService();
        Assert.Equal(IdempotencyDecision.New, service.Evaluate(null, "hash"));
        var record = IdempotencyRecord.Start(new IdempotencyKey("key"), "hash", Now);
        Assert.Equal(IdempotencyDecision.InProgress, service.Evaluate(record, "hash"));
        record.Complete("response", Now);
        Assert.Equal(IdempotencyDecision.Replay, service.Evaluate(record, "hash"));
        Assert.Throws<CoreConflictException>(() => service.Evaluate(record, "different"));
        Assert.Throws<CoreValidationException>(() => service.Evaluate(null, string.Empty));

        var cause = new InvalidOperationException("database race");
        var conflict = new CoreConflictException("duplicate", cause);
        Assert.Equal("duplicate", conflict.Message);
        Assert.Same(cause, conflict.InnerException);
    }

    [Fact]
    public async Task IngestionAcknowledgesOnlyAfterPublisherAndWrapsTransportFailure()
    {
        var publisher = new RecordingDurablePublisher();
        var service = new InboundValidationService(publisher, new CorrelationService());
        var command = new IngestionEventCommand(
            Guid.NewGuid(), "parcel.events.received", "key", "corr", new { Value = 1 }, Now);

        var receipt = await service.ValidateAndPublishAsync(command, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(command.EventId, receipt.EventId);
        var envelope = Assert.Single(publisher.Events);
        Assert.Equal("key", envelope.Key);
        Assert.Equal("corr", envelope.CorrelationId);

        var failing = new InboundValidationService(
            new RecordingDurablePublisher(new InvalidOperationException("broker unavailable")),
            new CorrelationService());
        await Assert.ThrowsAsync<CoreDependencyUnavailableException>(() => failing.ValidateAndPublishAsync(command, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreValidationException>(() => service.ValidateAndPublishAsync(command with { EventId = Guid.Empty }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IngestionAppliesOneMegabyteUtf8PayloadBoundaryBeforePublishing()
    {
        var publisher = new RecordingDurablePublisher();
        var service = new InboundValidationService(publisher, new CorrelationService());
        var command = new IngestionEventCommand(
            Guid.NewGuid(),
            "edge.integration-error.v1",
            "key",
            "corr",
            new string('x', IngestionPayloadLimits.MaximumUtf8Bytes - 2),
            Now);

        await service.ValidateAndPublishAsync(command, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(IngestionPayloadLimits.MaximumUtf8Bytes, Assert.Single(publisher.Events).Payload.Length);

        var tooLarge = command with
        {
            EventId = Guid.NewGuid(),
            Payload = new string('\u20ac', IngestionPayloadLimits.MaximumUtf8Bytes / 3)
        };
        await Assert.ThrowsAsync<CorePayloadTooLargeException>(
            () => service.ValidateAndPublishAsync(tooLarge, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(publisher.Events);
    }

    [Fact]
    public async Task OutboxPublisherMarksSuccessAndDeadLettersBoundedFailure()
    {
        var success = OutboxMessage.Create("topic", "{}", new CorrelationId("corr"), Now, 1);
        var successStore = new RecordingOutboxStore(success);
        var published = await new OutboxPublisher(
            successStore,
            new RecordingDurablePublisher(),
            new MutableTimeProvider(Now.AddMinutes(1))).PublishBatchAsync(10, 2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, published);
        Assert.Equal(OutboxStatus.Published, success.Status);
        Assert.Equal(1, successStore.Saves);

        var failed = OutboxMessage.Create("topic", "{}", new CorrelationId("corr"), Now);
        var failureStore = new RecordingOutboxStore(failed);
        const string sensitiveMarker = "private-upstream-host?credential=sentinel";
        var failurePublisher = new RecordingDurablePublisher(new InvalidOperationException(sensitiveMarker));
        var outbox = new OutboxPublisher(failureStore, failurePublisher, new MutableTimeProvider(Now));
        Assert.Equal(0, await outbox.PublishBatchAsync(1, 1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(OutboxStatus.DeadLetter, failed.Status);
        Assert.Equal(nameof(InvalidOperationException), failed.LastError);
        Assert.DoesNotContain(sensitiveMarker, failed.LastError, StringComparison.Ordinal);
        await Assert.ThrowsAsync<CoreValidationException>(() => outbox.PublishBatchAsync(0, 1, cancellationToken: TestContext.Current.CancellationToken));
    }
}
