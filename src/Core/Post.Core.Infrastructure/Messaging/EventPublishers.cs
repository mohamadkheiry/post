using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;

namespace Post.Core.Infrastructure.Messaging;

public sealed record KafkaPublishReceipt(
    string Topic,
    int Partition,
    long Offset,
    bool Persisted);

public interface IKafkaCompatibleProducer
{
    Task<KafkaPublishReceipt> ProduceAsync(
        string topic,
        string key,
        ReadOnlyMemory<byte> payload,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default);
}

public sealed class KafkaDurableEventPublisher : IDurableEventPublisher
{
    private readonly IKafkaCompatibleProducer _producer;
    private readonly TimeProvider _timeProvider;

    public KafkaDurableEventPublisher(IKafkaCompatibleProducer producer, TimeProvider timeProvider)
    {
        _producer = producer;
        _timeProvider = timeProvider;
    }

    public async Task<DurablePublishReceipt> PublishAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateEnvelope(envelope);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["event-id"] = envelope.EventId.ToString("D"),
            ["correlation-id"] = envelope.CorrelationId,
            ["occurred-at"] = envelope.OccurredAt.ToString("O"),
            ["schema-version"] = envelope.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(envelope.BusinessFingerprint))
        {
            headers["business-fingerprint"] = envelope.BusinessFingerprint;
        }
        var receipt = await _producer.ProduceAsync(
            envelope.Topic,
            envelope.Key,
            Encoding.UTF8.GetBytes(envelope.Payload),
            headers,
            cancellationToken).ConfigureAwait(false);
        if (!receipt.Persisted)
        {
            throw new CoreDependencyUnavailableException(
                "Kafka-compatible broker did not acknowledge durable persistence.");
        }

        return new DurablePublishReceipt(envelope.EventId, _timeProvider.GetUtcNow());
    }

    private static void ValidateEnvelope(EventEnvelope envelope)
    {
        if (envelope.EventId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.Topic) ||
            string.IsNullOrWhiteSpace(envelope.Key) || string.IsNullOrWhiteSpace(envelope.Payload) ||
            string.IsNullOrWhiteSpace(envelope.CorrelationId) || envelope.SchemaVersion <= 0 ||
            envelope.BusinessFingerprint is not null && string.IsNullOrWhiteSpace(envelope.BusinessFingerprint))
        {
            throw new CoreValidationException("A complete event envelope with a positive schema version is required.");
        }
    }
}

public sealed class InMemoryDurableEventPublisher : IDurableEventPublisher
{
    private readonly ConcurrentDictionary<Guid, StoredEvent> _events = new();
    private readonly TimeProvider _timeProvider;

    public InMemoryDurableEventPublisher(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public IReadOnlyCollection<EventEnvelope> Events =>
        _events.Values.OrderBy(x => x.AcknowledgedAt).Select(x => x.Envelope).ToArray();

    public Task<DurablePublishReceipt> PublishAsync(
        EventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = Fingerprint(envelope);
        var acknowledgedAt = _timeProvider.GetUtcNow();
        var stored = _events.GetOrAdd(
            envelope.EventId,
            _ => new StoredEvent(envelope, fingerprint, acknowledgedAt));
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(stored.Fingerprint),
                Convert.FromHexString(fingerprint)))
        {
            throw new CoreConflictException("An event id was replayed with a different envelope.");
        }

        return Task.FromResult(new DurablePublishReceipt(envelope.EventId, stored.AcknowledgedAt));
    }

    private static string Fingerprint(EventEnvelope envelope)
    {
        var canonical = string.Join(
            '\n',
            envelope.Topic,
            envelope.Key,
            envelope.BusinessFingerprint ?? envelope.Payload,
            envelope.OccurredAt.ToUniversalTime().ToString("O"),
            envelope.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record StoredEvent(EventEnvelope Envelope, string Fingerprint, DateTimeOffset AcknowledgedAt);
}
