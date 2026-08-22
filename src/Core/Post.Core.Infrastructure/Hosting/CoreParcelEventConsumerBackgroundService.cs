using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Infrastructure.Messaging;

namespace Post.Core.Infrastructure.Hosting;

public sealed record ParcelEventConsumerOptions
{
    public string Topic { get; init; } = "parcel.events.received";

    public string DeadLetterTopic { get; init; } = "parcel.events.received.dlq";

    public string GroupId { get; init; } = "post-core-parcel-lifecycle";

    public TimeSpan FailureRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public int MaximumDeliveryAttempts { get; init; } = 5;

    public int MaximumPayloadBytes { get; init; } = KafkaTransportOptions.MaximumSupportedPayloadBytes;
}

public sealed record ParcelEventDelivery(
    string Topic,
    int Partition,
    long Offset,
    string? Payload);

public enum ParcelEventDisposition
{
    Succeeded = 0,
    Retry = 1,
    DeadLettered = 2
}

public interface IParcelEventDeliveryProcessor
{
    Task<ParcelEventDisposition> ProcessAsync(
        ParcelEventDelivery delivery,
        int attempt,
        ParcelEventConsumerOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class ParcelEventDeliveryProcessor : IParcelEventDeliveryProcessor
{
    private readonly IParcelEventConsumer _handler;
    private readonly IKafkaCompatibleProducer _producer;
    private readonly TimeProvider _timeProvider;

    public ParcelEventDeliveryProcessor(
        IParcelEventConsumer handler,
        IKafkaCompatibleProducer producer,
        TimeProvider timeProvider)
    {
        _handler = handler;
        _producer = producer;
        _timeProvider = timeProvider;
    }

    public async Task<ParcelEventDisposition> ProcessAsync(
        ParcelEventDelivery delivery,
        int attempt,
        ParcelEventConsumerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(options);
        if (attempt <= 0 || options.MaximumDeliveryAttempts <= 0 ||
            options.MaximumPayloadBytes is < KafkaTransportOptions.MinimumPayloadBytes or
                > KafkaTransportOptions.MaximumSupportedPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt), "Delivery attempts must be positive.");
        }

        if (delivery.Payload is null)
        {
            return await RetryOrDeadLetterAsync(
                delivery,
                attempt,
                options,
                new InvalidDataException("Kafka tombstone payload is not a valid parcel event."),
                cancellationToken,
                forceDeadLetter: true,
                knownPayloadBytes: 0).ConfigureAwait(false);
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(delivery.Payload);
        if (payloadBytes > options.MaximumPayloadBytes)
        {
            return await RetryOrDeadLetterAsync(
                delivery,
                attempt,
                options,
                new InvalidDataException("Kafka payload exceeds the configured application limit."),
                cancellationToken,
                forceDeadLetter: true,
                knownPayloadBytes: payloadBytes).ConfigureAwait(false);
        }

        try
        {
            var command = JsonSerializer.Deserialize<ParcelFinalEventCommand>(delivery.Payload)
                ?? throw new JsonException("Parcel event payload is empty.");
            await _handler.ConsumeAsync(command, cancellationToken).ConfigureAwait(false);
            return ParcelEventDisposition.Succeeded;
        }
        catch (JsonException exception)
        {
            return await RetryOrDeadLetterAsync(delivery, attempt, options, exception, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CoreApplicationException exception)
        {
            return await RetryOrDeadLetterAsync(delivery, attempt, options, exception, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DomainRuleException exception)
        {
            return await RetryOrDeadLetterAsync(delivery, attempt, options, exception, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            return await RetryOrDeadLetterAsync(delivery, attempt, options, exception, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DbUpdateException exception)
        {
            return await RetryOrDeadLetterAsync(delivery, attempt, options, exception, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            return await RetryOrDeadLetterAsync(delivery, attempt, options, exception, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<ParcelEventDisposition> RetryOrDeadLetterAsync(
        ParcelEventDelivery delivery,
        int attempt,
        ParcelEventConsumerOptions options,
        Exception exception,
        CancellationToken cancellationToken,
        bool forceDeadLetter = false,
        int? knownPayloadBytes = null)
    {
        if (!forceDeadLetter && attempt < options.MaximumDeliveryAttempts)
        {
            return ParcelEventDisposition.Retry;
        }

        var deadLetterId = DeterministicId(delivery);
        var payloadBytes = knownPayloadBytes ??
            (delivery.Payload is null ? 0 : Encoding.UTF8.GetByteCount(delivery.Payload));
        var captureLimit = Math.Min(64_000, options.MaximumPayloadBytes / 2);
        var captureOriginalPayload = payloadBytes <= captureLimit;
        var record = new
        {
            DeadLetterId = deadLetterId,
            OriginalTopic = delivery.Topic,
            delivery.Partition,
            delivery.Offset,
            OriginalPayload = captureOriginalPayload ? delivery.Payload : null,
            OriginalPayloadBytes = payloadBytes,
            OriginalPayloadOmitted = !captureOriginalPayload,
            ErrorType = exception.GetType().Name,
            Attempts = attempt,
            DeadLetteredAt = _timeProvider.GetUtcNow()
        };
        KafkaPublishReceipt receipt;
        try
        {
            receipt = await _producer.ProduceAsync(
                options.DeadLetterTopic,
                string.Concat(
                    delivery.Topic,
                    ":",
                    delivery.Partition.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ":",
                    delivery.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                JsonSerializer.SerializeToUtf8Bytes(record),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["dead-letter-id"] = deadLetterId.ToString("D"),
                    ["original-topic"] = delivery.Topic,
                    ["original-partition"] = delivery.Partition.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["original-offset"] = delivery.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["error-type"] = exception.GetType().Name
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception publishException) when (
            publishException is CoreDependencyUnavailableException or TimeoutException)
        {
            return ParcelEventDisposition.Retry;
        }
        return receipt.Persisted
            ? ParcelEventDisposition.DeadLettered
            : ParcelEventDisposition.Retry;
    }

    private static Guid DeterministicId(ParcelEventDelivery delivery)
    {
        var input = Encoding.UTF8.GetBytes(string.Concat(
            delivery.Topic,
            "\n",
            delivery.Partition.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "\n",
            delivery.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new Guid(SHA256.HashData(input).AsSpan(0, 16));
    }
}

public sealed class CoreParcelEventConsumerBackgroundService : BackgroundService
{
    private static readonly Action<ILogger, string, int, long, int, Exception?> LogConsumeFailure =
        LoggerMessage.Define<string, int, long, int>(
            LogLevel.Warning,
            new EventId(2, "CoreParcelConsumeFailure"),
            "Parcel event {Topic}/{Partition}/{Offset} attempt {Attempt} was not committed and will be retried.");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KafkaTransportOptions _transportOptions;
    private readonly ParcelEventConsumerOptions _consumerOptions;
    private readonly ILogger<CoreParcelEventConsumerBackgroundService> _logger;

    public CoreParcelEventConsumerBackgroundService(
        IServiceScopeFactory scopeFactory,
        KafkaTransportOptions transportOptions,
        ParcelEventConsumerOptions consumerOptions,
        ILogger<CoreParcelEventConsumerBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _transportOptions = transportOptions;
        _consumerOptions = consumerOptions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempts = new Dictionary<TopicPartitionOffset, int>();
        using var consumer = new ConsumerBuilder<string, string>(
            KafkaConfigurationFactory.CreateConsumer(_transportOptions, _consumerOptions.GroupId)).Build();
        consumer.Subscribe(_consumerOptions.Topic);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string> result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException)
                {
                    await DelayAsync(1, stoppingToken).ConfigureAwait(false);
                    continue;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                var offset = result.TopicPartitionOffset;
                var attempt = attempts.TryGetValue(offset, out var previousAttempt)
                    ? checked(previousAttempt + 1)
                    : 1;
                attempts[offset] = attempt;
                var delivery = new ParcelEventDelivery(
                    result.Topic,
                    result.Partition.Value,
                    result.Offset.Value,
                    result.Message.Value);
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IParcelEventDeliveryProcessor>();
                var disposition = await processor.ProcessAsync(
                    delivery,
                    attempt,
                    _consumerOptions,
                    stoppingToken).ConfigureAwait(false);
                if (disposition == ParcelEventDisposition.Retry)
                {
                    LogConsumeFailure(
                        _logger,
                        delivery.Topic,
                        delivery.Partition,
                        delivery.Offset,
                        attempt,
                        null);
                    consumer.Seek(offset);
                    await DelayAsync(attempt, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    // DB success or durable DLQ persistence is the only terminal acknowledgement path.
                    consumer.StoreOffset(result);
                    consumer.Commit(result);
                    attempts.Remove(offset);
                }
                catch (KafkaException)
                {
                    consumer.Seek(offset);
                    await DelayAsync(attempt, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            consumer.Close();
        }
    }

    private Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var multiplier = Math.Min(attempt, 30);
        return Task.Delay(_consumerOptions.FailureRetryDelay * multiplier, cancellationToken);
    }

}
