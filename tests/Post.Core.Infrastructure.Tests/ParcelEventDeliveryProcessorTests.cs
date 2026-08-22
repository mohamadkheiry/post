using System.Text.Json;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Infrastructure.Hosting;
using Post.Core.Infrastructure.Messaging;

namespace Post.Core.Infrastructure.Tests;

public sealed class ParcelEventDeliveryProcessorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly ParcelEventConsumerOptions Options = new()
    {
        MaximumDeliveryAttempts = 3,
        DeadLetterTopic = "parcel.events.received.dlq"
    };

    [Fact]
    public async Task SuccessfulDatabaseHandlingReturnsTerminalCommitDisposition()
    {
        var handler = new StubParcelConsumer();
        var producer = new RecordingProducer(persisted: true);
        var processor = new ParcelEventDeliveryProcessor(handler, producer, new FixedTimeProvider(Now));

        var disposition = await processor.ProcessAsync(ValidDelivery(), 1, Options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ParcelEventDisposition.Succeeded, disposition);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, producer.Calls);
    }

    [Fact]
    public async Task TransientFailureReturnsRetryWithoutDurableAcknowledgement()
    {
        var handler = new StubParcelConsumer(new CoreDependencyUnavailableException("database offline"));
        var producer = new RecordingProducer(persisted: true);
        var processor = new ParcelEventDeliveryProcessor(handler, producer, new FixedTimeProvider(Now));

        var disposition = await processor.ProcessAsync(ValidDelivery(), 1, Options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ParcelEventDisposition.Retry, disposition);
        Assert.Equal(0, producer.Calls);
    }

    [Fact]
    public async Task PoisonMessageIsCommittedOnlyAfterDurableDeadLetterPersistence()
    {
        var producer = new RecordingProducer(persisted: true);
        var processor = new ParcelEventDeliveryProcessor(
            new StubParcelConsumer(),
            producer,
            new FixedTimeProvider(Now));
        var delivery = new ParcelEventDelivery("parcel.events.received", 2, 42, "malformed-json");

        var disposition = await processor.ProcessAsync(delivery, 3, Options, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ParcelEventDisposition.DeadLettered, disposition);
        Assert.Equal(Options.DeadLetterTopic, producer.Topic);
        Assert.Equal("2", producer.Headers!["original-partition"]);
        Assert.Equal("42", producer.Headers["original-offset"]);
        Assert.DoesNotContain("malformed-json", producer.Headers.Values);
        using var deadLetter = JsonDocument.Parse(producer.Payload);
        Assert.Equal("JsonException", deadLetter.RootElement.GetProperty("ErrorType").GetString());

        var unavailableProducer = new RecordingProducer(persisted: false);
        var retryingProcessor = new ParcelEventDeliveryProcessor(
            new StubParcelConsumer(),
            unavailableProducer,
            new FixedTimeProvider(Now));
        Assert.Equal(
            ParcelEventDisposition.Retry,
            await retryingProcessor.ProcessAsync(delivery, 3, Options, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Utf8PayloadBoundaryIsAcceptedAndOversizedPayloadDeadLettersImmediately()
    {
        var valid = ValidDelivery();
        var paddedPayload = valid.Payload + new string(
            ' ',
            KafkaTransportOptions.MaximumSupportedPayloadBytes -
            System.Text.Encoding.UTF8.GetByteCount(valid.Payload!));
        var handler = new StubParcelConsumer();
        var producer = new RecordingProducer(persisted: true);
        var processor = new ParcelEventDeliveryProcessor(handler, producer, new FixedTimeProvider(Now));

        Assert.Equal(
            ParcelEventDisposition.Succeeded,
            await processor.ProcessAsync(valid with { Payload = paddedPayload }, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);

        var oversized = valid with
        {
            Payload = new string('x', KafkaTransportOptions.MaximumSupportedPayloadBytes + 1)
        };
        Assert.Equal(ParcelEventDisposition.DeadLettered, await processor.ProcessAsync(oversized, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, producer.Calls);
        using var record = JsonDocument.Parse(producer.Payload);
        Assert.True(record.RootElement.GetProperty("OriginalPayloadOmitted").GetBoolean());
        Assert.Equal(
            KafkaTransportOptions.MaximumSupportedPayloadBytes + 1,
            record.RootElement.GetProperty("OriginalPayloadBytes").GetInt32());
        Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("OriginalPayload").ValueKind);

        var unavailable = new ParcelEventDeliveryProcessor(
            handler,
            new RecordingProducer(persisted: false),
            new FixedTimeProvider(Now));
        Assert.Equal(ParcelEventDisposition.Retry, await unavailable.ProcessAsync(oversized, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TombstoneDeadLettersImmediatelyAndDlqPublishFailureRemainsRetryable()
    {
        var persisted = new RecordingProducer(persisted: true);
        var processor = new ParcelEventDeliveryProcessor(
            new StubParcelConsumer(),
            persisted,
            new FixedTimeProvider(Now));
        var tombstone = new ParcelEventDelivery("parcel.events.received", 1, 9, null);

        Assert.Equal(ParcelEventDisposition.DeadLettered, await processor.ProcessAsync(tombstone, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
        using var record = JsonDocument.Parse(persisted.Payload);
        Assert.Equal(0, record.RootElement.GetProperty("OriginalPayloadBytes").GetInt32());
        Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("OriginalPayload").ValueKind);

        var unavailable = new ParcelEventDeliveryProcessor(
            new StubParcelConsumer(),
            new ThrowingProducer(),
            new FixedTimeProvider(Now));
        Assert.Equal(ParcelEventDisposition.Retry, await unavailable.ProcessAsync(tombstone, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static ParcelEventDelivery ValidDelivery()
    {
        var command = new ParcelFinalEventCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "C1",
            "key",
            "123456789012345678901234",
            CoreParcelStatus.Success,
            "corr",
            Now);
        return new ParcelEventDelivery(
            "parcel.events.received",
            0,
            1,
            JsonSerializer.Serialize(command));
    }

    private sealed class StubParcelConsumer(Exception? exception = null) : IParcelEventConsumer
    {
        public int Calls { get; private set; }

        public Task<bool> ConsumeAsync(
            ParcelFinalEventCommand command,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return exception is null
                ? Task.FromResult(true)
                : Task.FromException<bool>(exception);
        }
    }

    private sealed class RecordingProducer(bool persisted) : IKafkaCompatibleProducer
    {
        public int Calls { get; private set; }

        public string? Topic { get; private set; }

        public byte[] Payload { get; private set; } = [];

        public IReadOnlyDictionary<string, string>? Headers { get; private set; }

        public Task<KafkaPublishReceipt> ProduceAsync(
            string topic,
            string key,
            ReadOnlyMemory<byte> payload,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Topic = topic;
            Payload = payload.ToArray();
            Headers = headers;
            return Task.FromResult(new KafkaPublishReceipt(topic, 0, Calls, persisted));
        }
    }

    private sealed class ThrowingProducer : IKafkaCompatibleProducer
    {
        public Task<KafkaPublishReceipt> ProduceAsync(
            string topic,
            string key,
            ReadOnlyMemory<byte> payload,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken = default) =>
            Task.FromException<KafkaPublishReceipt>(
                new CoreDependencyUnavailableException("synthetic DLQ outage"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
