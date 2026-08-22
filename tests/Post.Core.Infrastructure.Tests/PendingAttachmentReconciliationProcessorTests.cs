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

public sealed class PendingAttachmentReconciliationProcessorTests
{
    private const string Barcode = "123456789012345678901234";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DueSupplementaryJsonLinksExactlyOnceAndRemovesPendingRow()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var inbound = await CreateInboundService(store, new MutableTimeProvider(Now)).ExecuteAsync(
            InboundCommand("reading"),
            cancellationToken: TestContext.Current.CancellationToken);
        var command = AttachmentCommand(inbound.ReadingRecordId);
        var hasher = new CanonicalJsonRequestHasher();
        store.AddPendingAttachment(CreatePending(command, hasher, Now));
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        var producer = new RecordingProducer(persisted: true);
        var processor = CreateProcessor(store, hasher, producer, new MutableTimeProvider(Now));

        var result = await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new PendingAttachmentReconciliationResult(1, 1, 0, 0, 0), result);
        Assert.Empty(await database.ParcelLifecycle.PendingAttachments.ToArrayAsync(TestContext.Current.CancellationToken));
        var attachment = Assert.Single(await database.ParcelLifecycle.SupplementaryAttachments.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(command.ReadingRecordId, attachment.ReadingRecordId);
        Assert.Equal("application/json", attachment.ContentType);
        Assert.Equal(0, producer.Calls);
        Assert.Equal(0, (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).Examined);
    }

    [Fact]
    public async Task MissingReadingUsesExponentialBackoffThenRetainedDeadLetterState()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var command = AttachmentCommand(Guid.NewGuid());
        var hasher = new CanonicalJsonRequestHasher();
        store.AddPendingAttachment(CreatePending(command, hasher, Now));
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        var time = new MutableTimeProvider(Now);
        var producer = new RecordingProducer(persisted: true);
        var options = Options() with { MaximumAttempts = 2 };
        var processor = CreateProcessor(store, hasher, producer, time, options);

        Assert.Equal(1, (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).Deferred);
        var pending = await store.FindPendingAttachmentByEventAsync(command.EventId, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(pending);
        Assert.Equal(1, pending.Attempts);
        Assert.Equal(Now.AddSeconds(5), pending.NextAttemptAt);
        Assert.Equal(0, (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).Examined);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).Deferred);
        Assert.Equal(2, pending.Attempts);
        Assert.Equal(Now.AddSeconds(15), pending.NextAttemptAt);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).DeadLettered);
        Assert.NotNull(pending.DeadLetteredAt);
        Assert.Null(pending.NextAttemptAt);
        Assert.Equal("MaximumAttemptsExceeded", pending.LastError);
        Assert.Equal(1, producer.Calls);
        Assert.Equal(options.DeadLetterTopic, producer.Topic);
        Assert.DoesNotContain(Barcode, producer.Headers!.Values);
        using var deadLetter = JsonDocument.Parse(producer.Payload);
        Assert.Equal(
            command.EventId,
            deadLetter.RootElement.GetProperty("EventId").GetGuid());
        Assert.Equal(0, (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).Examined);
        Assert.Equal(1, producer.Calls);
    }

    [Fact]
    public async Task NonPersistedDeadLetterIsRetriedAndNeverDeletesEvidence()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var command = AttachmentCommand(Guid.NewGuid());
        var hasher = new CanonicalJsonRequestHasher();
        var pending = CreatePending(command, hasher, Now);
        pending.ScheduleRetry(Now, TimeSpan.Zero, "first-attempt");
        store.AddPendingAttachment(pending);
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        var producer = new RecordingProducer(persisted: false);
        var processor = CreateProcessor(
            store,
            hasher,
            producer,
            new MutableTimeProvider(Now),
            Options() with { MaximumAttempts = 1 });

        var result = await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.DeadLettered);
        Assert.Equal(2, pending.Attempts);
        Assert.Null(pending.DeadLetteredAt);
        Assert.NotNull(await store.FindPendingAttachmentByEventAsync(command.EventId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("DeadLetterPublishNotPersisted", pending.LastError);
    }

    [Fact]
    public async Task InvalidDurableEnvelopeDeadLettersWithoutAttemptingLink()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var command = AttachmentCommand(Guid.NewGuid());
        var pending = PendingAttachment.Create(
            command.EventId,
            "hash",
            "not-json",
            new ParcelBarcode(command.ParcelBarcode),
            command.ReadingRecordId,
            command.ObjectKey,
            command.ContentType,
            command.AttachmentType,
            command.CorrelationId,
            command.ReceivedAt,
            Now);
        store.AddPendingAttachment(pending);
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        var producer = new RecordingProducer(persisted: true);
        var processor = CreateProcessor(
            store,
            new CanonicalJsonRequestHasher(),
            producer,
            new MutableTimeProvider(Now));

        var result = await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DeadLettered);
        Assert.Equal(nameof(JsonException), pending.LastError);
        Assert.NotNull(pending.DeadLetteredAt);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("reading")]
    [InlineData("barcode")]
    [InlineData("object-key")]
    [InlineData("content-type")]
    [InlineData("attachment-type")]
    public async Task TamperedDurableIndexFieldsAreRetainedAndDeadLettered(string field)
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var command = AttachmentCommand(Guid.NewGuid());
        var tampered = field switch
        {
            "event" => command with { EventId = Guid.NewGuid() },
            "reading" => command with { ReadingRecordId = Guid.NewGuid() },
            "barcode" => command with { ParcelBarcode = "999999999999999999999999" },
            "object-key" => command with { ObjectKey = string.Concat(command.ObjectKey, ".tampered") },
            "content-type" => command with { ContentType = "application/problem+json" },
            "attachment-type" => command with { AttachmentType = "tampered" },
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown tamper case.")
        };
        var pending = PendingAttachment.Create(
            command.EventId,
            "original-hash",
            JsonSerializer.Serialize(tampered),
            new ParcelBarcode(command.ParcelBarcode),
            command.ReadingRecordId,
            command.ObjectKey,
            command.ContentType,
            command.AttachmentType,
            command.CorrelationId,
            command.ReceivedAt,
            Now);
        store.AddPendingAttachment(pending);
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        var producer = new RecordingProducer(persisted: true);
        var processor = CreateProcessor(
            store,
            new CanonicalJsonRequestHasher(),
            producer,
            new MutableTimeProvider(Now));

        var result = await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.DeadLettered);
        Assert.Equal(nameof(CoreValidationException), pending.LastError);
        Assert.NotNull(await store.FindPendingAttachmentByEventAsync(command.EventId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, producer.Calls);
    }

    [Fact]
    public async Task ExpiredItemIsDeadLetteredOnceAndRetainedForAnExplicitRetentionPolicy()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var command = AttachmentCommand(Guid.NewGuid());
        var hasher = new CanonicalJsonRequestHasher();
        var pending = CreatePending(command, hasher, Now.AddHours(-2));
        store.AddPendingAttachment(pending);
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        var producer = new RecordingProducer(persisted: true);
        var options = Options() with { MaximumAge = TimeSpan.FromHours(1) };
        var processor = CreateProcessor(store, hasher, producer, new MutableTimeProvider(Now), options);

        var result = await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new PendingAttachmentReconciliationResult(1, 0, 0, 0, 1), result);
        Assert.Equal("MaximumAgeExceeded", pending.LastError);
        Assert.Equal(Now, pending.DeadLetteredAt);
        Assert.Null(pending.NextAttemptAt);
        Assert.NotNull(await store.FindPendingAttachmentByEventAsync(command.EventId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, producer.Calls);
        Assert.Equal(0, (await processor.ProcessBatchAsync(TestContext.Current.CancellationToken)).Examined);
        Assert.Equal(1, producer.Calls);
    }

    [Fact]
    public void OperationalDefaultsAreExplicitBoundedAndOverridable()
    {
        var defaults = new PendingAttachmentReconciliationOptions();

        Assert.Equal(TimeSpan.FromSeconds(30), defaults.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(5), defaults.BaseRetryDelay);
        Assert.Equal(TimeSpan.FromMinutes(5), defaults.MaximumRetryDelay);
        Assert.Equal(10, defaults.MaximumAttempts);
        Assert.Equal(TimeSpan.FromHours(24), defaults.MaximumAge);
        Assert.Equal(50, defaults.BatchSize);
        Assert.Equal("image.metadata.pending.dlq", defaults.DeadLetterTopic);
    }

    private static PendingAttachmentReconciliationProcessor CreateProcessor(
        EfParcelLifecycleStore store,
        CanonicalJsonRequestHasher hasher,
        IKafkaCompatibleProducer producer,
        TimeProvider timeProvider,
        PendingAttachmentReconciliationOptions? options = null) =>
        new(
            store,
            new AttachmentLinkingService(store, hasher, timeProvider),
            producer,
            timeProvider,
            options ?? Options());

    private static PendingAttachmentReconciliationOptions Options() => new()
    {
        PollingInterval = TimeSpan.FromSeconds(30),
        BaseRetryDelay = TimeSpan.FromSeconds(5),
        MaximumRetryDelay = TimeSpan.FromSeconds(20),
        MaximumAttempts = 3,
        MaximumAge = TimeSpan.FromHours(24),
        BatchSize = 10,
        DeadLetterTopic = "image.metadata.pending.dlq"
    };

    private static ImageMetadataCommand AttachmentCommand(Guid readingId)
    {
        var edgeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        return new ImageMetadataCommand(
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
    }

    private static PendingAttachment CreatePending(
        ImageMetadataCommand command,
        CanonicalJsonRequestHasher hasher,
        DateTimeOffset createdAt)
    {
        var hash = hasher.Hash(new
        {
            command.EventId,
            command.EdgeId,
            command.ExchangeCenterCode,
            command.ParcelBarcode,
            command.ReadingRecordId,
            command.ObjectKey,
            command.ContentType,
            command.AttachmentType,
            command.ReceivedAt
        });
        return PendingAttachment.Create(
            command.EventId,
            hash,
            JsonSerializer.Serialize(command),
            new ParcelBarcode(command.ParcelBarcode),
            command.ReadingRecordId,
            command.ObjectKey,
            command.ContentType,
            command.AttachmentType,
            command.CorrelationId,
            command.ReceivedAt,
            createdAt);
    }

    private static InboundQueryService CreateInboundService(
        EfParcelLifecycleStore store,
        TimeProvider timeProvider) =>
        new(
            store,
            new BarcodeValidator(BarcodeValidationPolicy.Default),
            new DuplicateReadingClassifier(),
            new DiscrepancyChecker(),
            new CoreDeviceStatusMapper(),
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            timeProvider,
            new OperationalSettings(
                TimeSpan.FromHours(6),
                TimeSpan.FromHours(72),
                new DiscrepancyTolerance(1, 2)));

    private static InboundQueryCommand InboundCommand(string key) => new(
        key,
        [Barcode],
        "parcel",
        "express",
        "C1",
        "C2",
        Guid.NewGuid(),
        "C1",
        new PhysicalAttributesDto(1, 2, 3, 4),
        Now,
        "corr");

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

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan interval) => _now += interval;
    }
}
