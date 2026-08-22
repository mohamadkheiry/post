using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Application.Parcels;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.ValueObjects;
using Post.Core.Infrastructure.Hosting;
using Post.Core.Infrastructure.Messaging;
using Post.Core.Infrastructure.Persistence;
using Post.Core.Infrastructure.Testing;

namespace Post.Core.Infrastructure.Tests;

public sealed class AttachmentMetadataDeliveryProcessorTests
{
    private const string Barcode = "123456789012345678901234";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AttachmentMetadataConsumerOptions Options = new()
    {
        MaximumDeliveryAttempts = 3,
        DeadLetterTopic = "image.metadata.received.dlq"
    };

    [Fact]
    public async Task SupplementaryJsonIsLinkedAndReplayDoesNotCreateAnotherAttachment()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var inbound = await CreateInboundService(store).ExecuteAsync(new InboundQueryCommand(
            "reading",
            [Barcode],
            "parcel",
            "express",
            "C1",
            "C2",
            Guid.NewGuid(),
            "C1",
            new PhysicalAttributesDto(1, 2, 3, 4),
            Now,
            "corr"), cancellationToken: TestContext.Current.CancellationToken);
        var producer = new RecordingProducer(persisted: true);
        var processor = new AttachmentMetadataDeliveryProcessor(
            new AttachmentLinkingService(store, new CanonicalJsonRequestHasher(), new FixedTimeProvider(Now)),
            store,
            producer,
            new FixedTimeProvider(Now));
        var edgeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var command = new ImageMetadataCommand(
            eventId,
            edgeId,
            "C1",
            Barcode,
            inbound.ReadingRecordId,
            EdgeUploadObjectKey.Create(edgeId, "C1", inbound.ReadingRecordId, eventId),
            "application/json",
            "supplementary-data",
            "corr",
            Now);
        var delivery = Delivery(JsonSerializer.Serialize(command));

        Assert.Equal(
            AttachmentMetadataDisposition.Succeeded,
            await processor.ProcessAsync(delivery, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            AttachmentMetadataDisposition.Succeeded,
            await processor.ProcessAsync(delivery, 1, Options, cancellationToken: TestContext.Current.CancellationToken));

        var attachment = Assert.Single(await database.ParcelLifecycle.SupplementaryAttachments.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(inbound.ReadingRecordId, attachment.ReadingRecordId);
        Assert.Equal("application/json", attachment.ContentType);
        Assert.Equal("supplementary-data", attachment.AttachmentType);
        Assert.Equal(0, producer.Calls);
    }

    [Fact]
    public async Task MissingReadingTransfersOwnershipToDurablePendingReconciliation()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var producer = new RecordingProducer(persisted: true);
        var processor = new AttachmentMetadataDeliveryProcessor(
            new AttachmentLinkingService(store, new CanonicalJsonRequestHasher(), new FixedTimeProvider(Now)),
            store,
            producer,
            new FixedTimeProvider(Now));
        var edgeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var readingId = Guid.NewGuid();
        var command = new ImageMetadataCommand(
            eventId,
            edgeId,
            "C1",
            Barcode,
            readingId,
            EdgeUploadObjectKey.Create(edgeId, "C1", readingId, eventId),
            "application/json",
            "supplementary-data",
            "corr",
            Now);
        var delivery = Delivery(JsonSerializer.Serialize(command));

        Assert.Equal(
            AttachmentMetadataDisposition.Succeeded,
            await processor.ProcessAsync(delivery, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
        var pending = Assert.Single(await database.ParcelLifecycle.PendingAttachments.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(command.EventId, pending.EventId);
        Assert.Equal(JsonSerializer.Serialize(command), pending.PayloadJson);
        Assert.Equal(
            AttachmentMetadataDisposition.Succeeded,
            await processor.ProcessAsync(delivery, 3, Options, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, producer.Calls);
    }

    [Fact]
    public async Task KafkaHandoffAndReconcilerRaceNeverDoubleLinksOrCreatesFalseDeadLetter()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var inbound = await CreateInboundService(store).ExecuteAsync(new InboundQueryCommand(
            "race-reading",
            [Barcode],
            "parcel",
            "express",
            "C1",
            "C2",
            Guid.NewGuid(),
            "C1",
            new PhysicalAttributesDto(1, 2, 3, 4),
            Now,
            "corr"), cancellationToken: TestContext.Current.CancellationToken);
        var delayedReadingId = Guid.NewGuid();
        var edgeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var command = new ImageMetadataCommand(
            eventId,
            edgeId,
            "C1",
            Barcode,
            delayedReadingId,
            EdgeUploadObjectKey.Create(edgeId, "C1", delayedReadingId, eventId),
            "application/json",
            "supplementary-data",
            "corr",
            Now);
        var delivery = Delivery(JsonSerializer.Serialize(command));
        var producer = new RecordingProducer(persisted: true);
        var hasher = new CanonicalJsonRequestHasher();
        var time = new FixedTimeProvider(Now);
        var consumer = new AttachmentMetadataDeliveryProcessor(
            new AttachmentLinkingService(store, hasher, time),
            store,
            producer,
            time);

        Assert.Equal(
            AttachmentMetadataDisposition.Succeeded,
            await consumer.ProcessAsync(delivery, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await database.ParcelLifecycle.PendingAttachments.ToArrayAsync(TestContext.Current.CancellationToken));

        database.ParcelLifecycle.ChangeTracker.Clear();
        await database.ParcelLifecycle.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ReadingRecords SET Id = {delayedReadingId} WHERE Id = {inbound.ReadingRecordId}", cancellationToken: TestContext.Current.CancellationToken);
        var reconciler = new PendingAttachmentReconciliationProcessor(
            store,
            new AttachmentLinkingService(store, hasher, time),
            producer,
            time,
            new PendingAttachmentReconciliationOptions
            {
                BatchSize = 10,
                MaximumAttempts = 3,
                MaximumAge = TimeSpan.FromHours(24),
                BaseRetryDelay = TimeSpan.FromSeconds(1),
                MaximumRetryDelay = TimeSpan.FromSeconds(10)
            });

        Assert.Equal(1, (await reconciler.ProcessBatchAsync(TestContext.Current.CancellationToken)).Linked);
        Assert.Equal(
            AttachmentMetadataDisposition.Succeeded,
            await consumer.ProcessAsync(delivery, 3, Options, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await database.ParcelLifecycle.SupplementaryAttachments.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await database.ParcelLifecycle.PendingAttachments.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, producer.Calls);
        Assert.Equal(0, (await reconciler.ProcessBatchAsync(TestContext.Current.CancellationToken)).Examined);
    }

    [Fact]
    public async Task PoisonPayloadDeadLettersAndInvalidAttemptsFailFast()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var producer = new RecordingProducer(persisted: true);
        var processor = new AttachmentMetadataDeliveryProcessor(
            new AttachmentLinkingService(store, new CanonicalJsonRequestHasher(), new FixedTimeProvider(Now)),
            store,
            producer,
            new FixedTimeProvider(Now));
        var delivery = Delivery("not-json");

        Assert.Equal(
            AttachmentMetadataDisposition.Retry,
            await processor.ProcessAsync(delivery, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(
            AttachmentMetadataDisposition.DeadLettered,
            await processor.ProcessAsync(delivery, 3, Options, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            processor.ProcessAsync(delivery, 0, Options, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Utf8PayloadBoundaryIsAcceptedAndOversizedPayloadDeadLettersImmediately()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var producer = new RecordingProducer(persisted: true);
        var processor = new AttachmentMetadataDeliveryProcessor(
            new AttachmentLinkingService(store, new CanonicalJsonRequestHasher(), new FixedTimeProvider(Now)),
            store,
            producer,
            new FixedTimeProvider(Now));
        var edgeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var readingId = Guid.NewGuid();
        var command = new ImageMetadataCommand(
            eventId,
            edgeId,
            "C1",
            Barcode,
            readingId,
            EdgeUploadObjectKey.Create(edgeId, "C1", readingId, eventId),
            "application/json",
            "supplementary-data",
            "corr",
            Now);
        var json = JsonSerializer.Serialize(command);
        var padded = json + new string(
            ' ',
            KafkaTransportOptions.MaximumSupportedPayloadBytes -
            System.Text.Encoding.UTF8.GetByteCount(json));

        Assert.Equal(
            AttachmentMetadataDisposition.Succeeded,
            await processor.ProcessAsync(Delivery(padded), 1, Options, cancellationToken: TestContext.Current.CancellationToken));

        var oversized = Delivery(new string('x', KafkaTransportOptions.MaximumSupportedPayloadBytes + 1));
        Assert.Equal(
            AttachmentMetadataDisposition.DeadLettered,
            await processor.ProcessAsync(oversized, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
        using var record = JsonDocument.Parse(producer.Payload);
        Assert.True(record.RootElement.GetProperty("OriginalPayloadOmitted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, record.RootElement.GetProperty("OriginalPayload").ValueKind);
    }

    [Fact]
    public async Task TombstoneDeadLettersImmediatelyAndDlqPublishFailureRemainsRetryable()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var persisted = new RecordingProducer(persisted: true);
        var processor = new AttachmentMetadataDeliveryProcessor(
            new AttachmentLinkingService(store, new CanonicalJsonRequestHasher(), new FixedTimeProvider(Now)),
            store,
            persisted,
            new FixedTimeProvider(Now));
        var tombstone = new AttachmentMetadataDelivery("image.metadata.received", 1, 9, null);

        Assert.Equal(
            AttachmentMetadataDisposition.DeadLettered,
            await processor.ProcessAsync(tombstone, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
        using var record = JsonDocument.Parse(persisted.Payload);
        Assert.Equal(0, record.RootElement.GetProperty("OriginalPayloadBytes").GetInt32());

        var unavailable = new AttachmentMetadataDeliveryProcessor(
            new AttachmentLinkingService(store, new CanonicalJsonRequestHasher(), new FixedTimeProvider(Now)),
            store,
            new ThrowingProducer(),
            new FixedTimeProvider(Now));
        Assert.Equal(AttachmentMetadataDisposition.Retry, await unavailable.ProcessAsync(tombstone, 1, Options, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static AttachmentMetadataDelivery Delivery(string payload) =>
        new("image.metadata.received", 0, 1, payload);

    private static InboundQueryService CreateInboundService(EfParcelLifecycleStore store) => new(
        store,
        new BarcodeValidator(BarcodeValidationPolicy.Default),
        new DuplicateReadingClassifier(),
        new DiscrepancyChecker(),
        new CoreDeviceStatusMapper(),
        new CanonicalJsonRequestHasher(),
        new IdempotencyService(),
        new FixedTimeProvider(Now),
        new OperationalSettings(
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(72),
            new DiscrepancyTolerance(1, 2)));

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
