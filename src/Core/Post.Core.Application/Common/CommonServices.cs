using System.Security.Cryptography;
using System.Text.Json;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Application.Common;

public sealed class CanonicalJsonRequestHasher : IRequestHasher
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public string Hash<T>(T request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Options);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

public sealed class CorrelationService : ICorrelationService
{
    public string GetOrCreate(string? suppliedCorrelationId)
    {
        if (string.IsNullOrWhiteSpace(suppliedCorrelationId))
        {
            return CorrelationId.New().Value;
        }

        return new CorrelationId(suppliedCorrelationId).Value;
    }
}

public sealed class IdempotencyService : IIdempotencyService
{
    public IdempotencyDecision Evaluate(IdempotencyRecord? existing, string requestHash)
    {
        if (string.IsNullOrWhiteSpace(requestHash))
        {
            throw new CoreValidationException("Request hash is required.");
        }

        if (existing is null)
        {
            return IdempotencyDecision.New;
        }

        if (!existing.HasSameRequest(requestHash))
        {
            throw new CoreConflictException("The idempotency key was already used with a different request payload.");
        }

        return existing.Status switch
        {
            IdempotencyStatus.Completed => IdempotencyDecision.Replay,
            IdempotencyStatus.InProgress => IdempotencyDecision.InProgress,
            IdempotencyStatus.Failed => IdempotencyDecision.New,
            _ => throw new ArgumentOutOfRangeException(nameof(existing), existing.Status, "Unknown idempotency status.")
        };
    }
}

public sealed class OutboxPublisher : IOutboxPublisher
{
    private readonly IOutboxStore _store;
    private readonly IDurableEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;

    public OutboxPublisher(IOutboxStore store, IDurableEventPublisher publisher, TimeProvider timeProvider)
    {
        _store = store;
        _publisher = publisher;
        _timeProvider = timeProvider;
    }

    public async Task<int> PublishBatchAsync(
        int maximumCount,
        int maximumAttempts,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount <= 0 || maximumAttempts <= 0)
        {
            throw new CoreValidationException("Batch size and maximum attempts must be positive.");
        }

        var messages = await _store.GetPendingAsync(maximumCount, cancellationToken).ConfigureAwait(false);
        var published = 0;
        foreach (var message in messages.OrderBy(x => x.Sequence).ThenBy(x => x.OccurredAt))
        {
            try
            {
                await _publisher.PublishAsync(
                    new EventEnvelope(
                        message.Id,
                        message.Topic,
                        message.Id.ToString("N"),
                        message.Payload,
                        message.CorrelationId.Value,
                        message.OccurredAt),
                    cancellationToken).ConfigureAwait(false);
                message.MarkPublished(_timeProvider.GetUtcNow());
                published++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Exception messages can contain dependency URLs, response bodies, or
                // credentials. Persist only a bounded diagnostic classification.
                message.RecordFailure(exception.GetType().Name, maximumAttempts);
            }
        }

        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return published;
    }
}
