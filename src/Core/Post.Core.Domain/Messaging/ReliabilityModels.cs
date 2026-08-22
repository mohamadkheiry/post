using Post.Core.Domain.Common;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Domain.Messaging;

public sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
        RequestHash = string.Empty;
        ResponseSnapshot = string.Empty;
    }

    private IdempotencyRecord(
        IdempotencyKey key,
        string requestHash,
        DateTimeOffset createdAt)
    {
        Key = key;
        RequestHash = Required(requestHash, nameof(requestHash), 128);
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        ResponseSnapshot = string.Empty;
        Status = IdempotencyStatus.InProgress;
    }

    public IdempotencyKey Key { get; private set; }

    public string RequestHash { get; private set; }

    public string ResponseSnapshot { get; private set; }

    public IdempotencyStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static IdempotencyRecord Start(
        IdempotencyKey key,
        string requestHash,
        DateTimeOffset createdAt) =>
        new(key, requestHash, createdAt);

    public bool HasSameRequest(string requestHash) =>
        string.Equals(RequestHash, requestHash, StringComparison.Ordinal);

    public void Complete(string responseSnapshot, DateTimeOffset completedAt)
    {
        if (Status == IdempotencyStatus.Completed)
        {
            if (!string.Equals(ResponseSnapshot, responseSnapshot, StringComparison.Ordinal))
            {
                throw new DomainRuleException("A completed idempotency record cannot change its response.");
            }

            return;
        }

        ResponseSnapshot = responseSnapshot ?? string.Empty;
        Status = IdempotencyStatus.Completed;
        UpdatedAt = completedAt;
    }

    public void Fail(DateTimeOffset failedAt)
    {
        if (Status == IdempotencyStatus.Completed)
        {
            throw new DomainRuleException("A completed idempotency record cannot be failed.");
        }

        Status = IdempotencyStatus.Failed;
        UpdatedAt = failedAt;
    }

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }
}

public sealed class InboxMessage
{
    private InboxMessage()
    {
        PayloadHash = string.Empty;
    }

    private InboxMessage(Guid eventId, string payloadHash, DateTimeOffset processedAt)
    {
        if (eventId == Guid.Empty)
        {
            throw new DomainRuleException("Inbox event identifier cannot be empty.");
        }

        EventId = eventId;
        PayloadHash = string.IsNullOrWhiteSpace(payloadHash)
            ? throw new DomainRuleException("Inbox payload hash is required.")
            : payloadHash;
        ProcessedAt = processedAt;
    }

    public Guid EventId { get; private set; }

    public string PayloadHash { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }

    public static InboxMessage Processed(Guid eventId, string payloadHash, DateTimeOffset processedAt) =>
        new(eventId, payloadHash, processedAt);

    public bool HasSamePayload(string payloadHash) =>
        string.Equals(PayloadHash, payloadHash, StringComparison.Ordinal);
}

public sealed class OutboxMessage
{
    private OutboxMessage()
    {
        Topic = string.Empty;
        Payload = string.Empty;
    }

    private OutboxMessage(
        Guid id,
        string topic,
        string payload,
        CorrelationId correlationId,
        DateTimeOffset occurredAt,
        long sequence)
    {
        if (sequence < 0)
        {
            throw new DomainRuleException("Outbox sequence cannot be negative.");
        }

        Id = id;
        Topic = Required(topic, nameof(topic), 256);
        Payload = Required(payload, nameof(payload), 1_000_000);
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
        Sequence = sequence;
        Status = OutboxStatus.Pending;
    }

    public Guid Id { get; private set; }

    public string Topic { get; private set; }

    public string Payload { get; private set; }

    public CorrelationId CorrelationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public long Sequence { get; private set; }

    public OutboxStatus Status { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxMessage Create(
        string topic,
        string payload,
        CorrelationId correlationId,
        DateTimeOffset occurredAt,
        long sequence = 0) =>
        new(Guid.NewGuid(), topic, payload, correlationId, occurredAt, sequence);

    public void MarkPublished(DateTimeOffset publishedAt)
    {
        Status = OutboxStatus.Published;
        PublishedAt = publishedAt;
        LastError = null;
    }

    public void RecordFailure(string error, int maximumAttempts)
    {
        if (maximumAttempts <= 0)
        {
            throw new DomainRuleException("Maximum attempts must be positive.");
        }

        Attempts = checked(Attempts + 1);
        LastError = string.IsNullOrWhiteSpace(error) ? "Unknown publish error" : error.Trim();
        if (Attempts >= maximumAttempts)
        {
            Status = OutboxStatus.DeadLetter;
        }
    }

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value;
    }
}
