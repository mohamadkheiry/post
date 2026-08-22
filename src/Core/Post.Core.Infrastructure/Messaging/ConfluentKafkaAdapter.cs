using System.Text;
using Confluent.Kafka;
using Post.Core.Application.Common;

namespace Post.Core.Infrastructure.Messaging;

public sealed record KafkaTransportOptions
{
    public const int MinimumPayloadBytes = 65_536;
    public const int MaximumSupportedPayloadBytes = IngestionPayloadLimits.MaximumUtf8Bytes;

    public required string BootstrapServers { get; init; }

    public string ClientId { get; init; } = "post-core";

    public SecurityProtocol SecurityProtocol { get; init; } = SecurityProtocol.Plaintext;

    public SaslMechanism? SaslMechanism { get; init; }

    public string? SaslUsername { get; init; }

    public string? SaslPassword { get; init; }

    public string? SslCaLocation { get; init; }

    public int MaximumPayloadBytes { get; init; } = MaximumSupportedPayloadBytes;

    public int DeliveryTimeoutMilliseconds { get; init; } = 30_000;

    public int SocketTimeoutMilliseconds { get; init; } = 10_000;

    public int MaximumSendRetries { get; init; } = 5;
}

public static class KafkaConfigurationFactory
{
    public static ProducerConfig CreateProducer(KafkaTransportOptions options)
    {
        Validate(options);
        return new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId,
            SecurityProtocol = options.SecurityProtocol,
            SaslMechanism = options.SaslMechanism,
            SaslUsername = options.SaslUsername,
            SaslPassword = options.SaslPassword,
            SslCaLocation = options.SslCaLocation,
            Acks = Acks.All,
            EnableIdempotence = true,
            MaxInFlight = 5,
            MessageSendMaxRetries = options.MaximumSendRetries,
            // Confluent.Kafka exposes librdkafka's delivery.timeout.ms through
            // MessageTimeoutMs in the managed ProducerConfig API.
            MessageTimeoutMs = options.DeliveryTimeoutMilliseconds,
            SocketTimeoutMs = options.SocketTimeoutMilliseconds,
            // librdkafka's limit includes protocol framing while the application limit applies
            // only to the value bytes. Keep a small, bounded allowance for framing.
            MessageMaxBytes = checked(options.MaximumPayloadBytes + 512)
        };
    }

    public static ConsumerConfig CreateConsumer(
        KafkaTransportOptions options,
        string groupId)
    {
        Validate(options);
        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new ArgumentException("Kafka consumer group id is required.", nameof(groupId));
        }

        return new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId,
            GroupId = groupId,
            SecurityProtocol = options.SecurityProtocol,
            SaslMechanism = options.SaslMechanism,
            SaslUsername = options.SaslUsername,
            SaslPassword = options.SaslPassword,
            SslCaLocation = options.SslCaLocation,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            IsolationLevel = IsolationLevel.ReadCommitted,
            SocketTimeoutMs = options.SocketTimeoutMilliseconds,
            MaxPartitionFetchBytes = options.MaximumPayloadBytes,
            FetchMaxBytes = options.MaximumPayloadBytes,
            ReceiveMessageMaxBytes = checked(options.MaximumPayloadBytes + 512)
        };
    }

    private static void Validate(KafkaTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.BootstrapServers) || string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new ArgumentException("Kafka bootstrap servers and client id are required.", nameof(options));
        }

        if (options.MaximumPayloadBytes is < KafkaTransportOptions.MinimumPayloadBytes or
            > KafkaTransportOptions.MaximumSupportedPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"Kafka maximum payload bytes must be between {KafkaTransportOptions.MinimumPayloadBytes} and " +
                $"{KafkaTransportOptions.MaximumSupportedPayloadBytes}.");
        }

        if (options.DeliveryTimeoutMilliseconds is < 1_000 or > 120_000 ||
            options.SocketTimeoutMilliseconds is < 1_000 or > 60_000 ||
            options.MaximumSendRetries is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Kafka delivery/socket timeouts or retry count are outside their safe ranges.");
        }

        var sasl = options.SecurityProtocol is SecurityProtocol.SaslPlaintext or SecurityProtocol.SaslSsl;
        if (sasl && (options.SaslMechanism is null || string.IsNullOrWhiteSpace(options.SaslUsername) ||
                     string.IsNullOrWhiteSpace(options.SaslPassword)))
        {
            throw new ArgumentException("SASL protocol requires mechanism, user name, and password.", nameof(options));
        }
    }
}

public sealed class ConfluentKafkaProducer : IKafkaCompatibleProducer, IDisposable
{
    private readonly int _maximumPayloadBytes;
    private IProducer<string, byte[]>? _producer;

    public ConfluentKafkaProducer(KafkaTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _maximumPayloadBytes = options.MaximumPayloadBytes;
        _producer = new ProducerBuilder<string, byte[]>(KafkaConfigurationFactory.CreateProducer(options)).Build();
    }

    public async Task<KafkaPublishReceipt> ProduceAsync(
        string topic,
        string key,
        ReadOnlyMemory<byte> payload,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Kafka topic and key are required.", nameof(topic));
        }

        if (payload.Length > _maximumPayloadBytes)
        {
            throw new CorePayloadTooLargeException(
                $"Kafka payload must not exceed {_maximumPayloadBytes} UTF-8 bytes.");
        }

        ArgumentNullException.ThrowIfNull(headers);
        var producer = _producer ?? throw new ObjectDisposedException(nameof(ConfluentKafkaProducer));
        var kafkaHeaders = new Headers();
        foreach (var header in headers)
        {
            kafkaHeaders.Add(header.Key, Encoding.UTF8.GetBytes(header.Value));
        }

        try
        {
            var delivery = await producer.ProduceAsync(
                topic,
                new Message<string, byte[]>
                {
                    Key = key,
                    Value = payload.ToArray(),
                    Headers = kafkaHeaders
                },
                cancellationToken).ConfigureAwait(false);
            return new KafkaPublishReceipt(
                delivery.Topic,
                delivery.Partition.Value,
                delivery.Offset.Value,
                delivery.Status == PersistenceStatus.Persisted);
        }
        catch (KafkaException exception)
        {
            throw new CoreDependencyUnavailableException("Kafka-compatible publish failed.", exception);
        }
    }

    public void Dispose()
    {
        var producer = Interlocked.Exchange(ref _producer, null);
        producer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
