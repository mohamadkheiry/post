using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Application.Parcels;
using Post.Core.Domain.Common;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.ValueObjects;
using Post.Core.Infrastructure.Persistence;
using Post.Core.Infrastructure.Testing;

namespace Post.Core.Application.Tests;

public sealed class ParcelServiceTests
{
    private const string Barcode = "123456789012345678901234";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InboundQueryCommitsThenReplaysAndRejectsDifferentPayloadForSameKey()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var service = CreateInboundService(new EfParcelLifecycleStore(database.ParcelLifecycle));
        var command = InboundCommand("key-1", Now, new PhysicalAttributesDto(10, 20, 30, 40));

        var first = await service.ExecuteAsync(command, cancellationToken: TestContext.Current.CancellationToken);
        var replay = await service.ExecuteAsync(command, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.ReadingRecordId, replay.ReadingRecordId);
        Assert.Equal(1, await database.ParcelLifecycle.ParcelDossiers.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.ParcelLifecycle.ReadingRecords.CountAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreConflictException>(() => service.ExecuteAsync(
            command with { PhysicalAttributes = new PhysicalAttributesDto(11, 20, 30, 40) }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InboundQueryUsesExactReturnBoundaryAndReportsOnlyBeyondTolerance()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var service = CreateInboundService(new EfParcelLifecycleStore(database.ParcelLifecycle));
        await service.ExecuteAsync(InboundCommand("first", Now, new PhysicalAttributesDto(10, 20, 30, 40)), cancellationToken: TestContext.Current.CancellationToken);

        var returned = await service.ExecuteAsync(InboundCommand(
            "second",
            Now.AddHours(6),
            new PhysicalAttributesDto(11, 22.01m, 28, 42)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(CoreParcelStatus.Returned, returned.CoreStatus);
        Assert.Equal(DeviceParcelStatus.Returned, returned.DeviceStatus);
        var discrepancy = Assert.Single(returned.Discrepancies);
        Assert.Equal(PhysicalAttributeKind.Length, discrepancy.Attribute);
    }

    [Fact]
    public async Task EventConsumerIsInboxIdempotentAndAttachmentCanArriveBeforeOrAfterReading()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var inbound = CreateInboundService(store);
        var inboundResult = await inbound.ExecuteAsync(InboundCommand(
            "reading", Now, new PhysicalAttributesDto(1, 2, 3, 4)), cancellationToken: TestContext.Current.CancellationToken);
        var eventConsumer = new ParcelEventConsumer(
            store, new CanonicalJsonRequestHasher(), new IdempotencyService(), new MutableTimeProvider(Now));
        var edgeId = Guid.NewGuid();
        var final = new ParcelFinalEventCommand(
            Guid.NewGuid(), edgeId, "C1", "final", Barcode, CoreParcelStatus.Success, "corr", Now);

        Assert.True(await eventConsumer.ConsumeAsync(final, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await eventConsumer.ConsumeAsync(final with { CorrelationId = "retry-correlation" }, cancellationToken: TestContext.Current.CancellationToken));
        var conflictingFinalEvents = new[]
        {
            final with { IdempotencyKey = "different-key" },
            final with { EdgeId = Guid.NewGuid() },
            final with { ExchangeCenterCode = "C2" },
            final with { ParcelBarcode = "999999999999999999999999" },
            final with { FinalStatus = CoreParcelStatus.Error },
            final with { OccurredAt = Now.AddSeconds(1) }
        };
        foreach (var conflictingEvent in conflictingFinalEvents)
        {
            await Assert.ThrowsAsync<CoreConflictException>(() => eventConsumer.ConsumeAsync(conflictingEvent, cancellationToken: TestContext.Current.CancellationToken));
        }

        await Assert.ThrowsAsync<CoreAuthorizationException>(() => eventConsumer.ConsumeAsync(
            final with
            {
                EventId = Guid.NewGuid(),
                IdempotencyKey = "cross-center-final",
                ExchangeCenterCode = "C2"
            }, cancellationToken: TestContext.Current.CancellationToken));

        var attachment = new AttachmentLinkingService(
            store, new CanonicalJsonRequestHasher(), new MutableTimeProvider(Now));
        var pendingEventId = Guid.NewGuid();
        var pendingReadingId = Guid.NewGuid();
        var pending = new ImageMetadataCommand(
            pendingEventId,
            edgeId,
            "C1",
            Barcode,
            pendingReadingId,
            EdgeUploadObjectKey.Create(edgeId, "C1", pendingReadingId, pendingEventId),
            "image/jpeg",
            "parcel",
            "corr",
            Now);
        Assert.False(await attachment.LinkAsync(pending, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await store.FindPendingAttachmentAsync(pending.ReadingRecordId, pending.ObjectKey, cancellationToken: TestContext.Current.CancellationToken));

        var linkedEventId = Guid.NewGuid();
        var linked = new ImageMetadataCommand(
            linkedEventId,
            edgeId,
            "C1",
            Barcode,
            inboundResult.ReadingRecordId,
            EdgeUploadObjectKey.Create(edgeId, "C1", inboundResult.ReadingRecordId, linkedEventId),
            "image/jpeg",
            "parcel",
            "corr",
            Now);
        Assert.True(await attachment.LinkAsync(linked, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await attachment.LinkAsync(linked with { CorrelationId = "retry-correlation" }, cancellationToken: TestContext.Current.CancellationToken));
        var conflictingAttachments = new[]
        {
            linked with { EdgeId = Guid.NewGuid() },
            linked with { ExchangeCenterCode = "C2" },
            linked with { ParcelBarcode = "999999999999999999999999" },
            linked with { ReadingRecordId = Guid.NewGuid() },
            linked with { ObjectKey = "different.jpg" },
            linked with { ContentType = "image/png" },
            linked with { AttachmentType = "different" },
            linked with { ReceivedAt = Now.AddSeconds(1) }
        };
        foreach (var conflictingAttachment in conflictingAttachments)
        {
            await Assert.ThrowsAsync<CoreConflictException>(() => attachment.LinkAsync(conflictingAttachment, cancellationToken: TestContext.Current.CancellationToken));
        }


        var crossCenterEventId = Guid.NewGuid();
        await Assert.ThrowsAsync<CoreAuthorizationException>(() => attachment.LinkAsync(
            linked with
            {
                EventId = crossCenterEventId,
                ExchangeCenterCode = "C2",
                ObjectKey = EdgeUploadObjectKey.Create(
                    edgeId,
                    "C2",
                    inboundResult.ReadingRecordId,
                    crossCenterEventId)
            }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SupplementaryJsonAttachmentLinksToOwningReadingAndIsExactlyOnce()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var inbound = await CreateInboundService(store).ExecuteAsync(
            InboundCommand("supplementary-reading", Now, new PhysicalAttributesDto(1, 2, 3, 4)),
            cancellationToken: TestContext.Current.CancellationToken);
        var hasher = new CanonicalJsonRequestHasher();
        var service = new AttachmentLinkingService(store, hasher, new MutableTimeProvider(Now));
        var edgeId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var objectKey = EdgeUploadObjectKey.Create(edgeId, "C1", inbound.ReadingRecordId, eventId);
        var command = new ImageMetadataCommand(
            eventId,
            edgeId,
            "C1",
            Barcode,
            inbound.ReadingRecordId,
            objectKey,
            "application/json",
            "supplementary-data",
            "corr",
            Now);
        var requestHash = hasher.Hash(new
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
        store.AddPendingAttachment(PendingAttachment.Create(
            eventId,
            requestHash,
            JsonSerializer.Serialize(command),
            new ParcelBarcode(Barcode),
            inbound.ReadingRecordId,
            objectKey,
            "application/json",
            "supplementary-data",
            "corr",
            Now,
            Now));
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(await service.LinkAsync(command, cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await service.LinkAsync(command, cancellationToken: TestContext.Current.CancellationToken));
        var persisted = Assert.Single(await database.ParcelLifecycle.SupplementaryAttachments.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("application/json", persisted.ContentType);
        Assert.Equal("supplementary-data", persisted.AttachmentType);
        Assert.Empty(await database.ParcelLifecycle.PendingAttachments.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await database.ParcelLifecycle.InboxMessages.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Single(await database.ParcelLifecycle.OutboxMessages
            .Where(message => message.Topic == "parcel.lifecycle.attachment-linked")
            .ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<CoreConflictException>(() => service.LinkAsync(
            command with { ContentType = "application/problem+json" }, cancellationToken: TestContext.Current.CancellationToken));
        var wrongParcelEventId = Guid.NewGuid();
        await Assert.ThrowsAsync<CoreValidationException>(() => service.LinkAsync(
            command with
            {
                EventId = wrongParcelEventId,
                ParcelBarcode = "999999999999999999999999",
                ObjectKey = EdgeUploadObjectKey.Create(
                    edgeId,
                    "C1",
                    command.ReadingRecordId,
                    wrongParcelEventId)
            }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreValidationException>(() => service.LinkAsync(
            command with { EventId = Guid.Empty }, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TrackingAndBagDispatchQueriesEnforceCenterScope()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        await CreateInboundService(store).ExecuteAsync(
            InboundCommand("reading", Now, new PhysicalAttributesDto(1, 2, 3, 4)),
            cancellationToken: TestContext.Current.CancellationToken);
        var tracking = new ParcelTrackingQueryService(store);

        Assert.NotNull(await tracking.FindAsync(Barcode, new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreAuthorizationException>(() =>
            tracking.FindAsync(Barcode, new AccessScope(false, ["NO"]), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await tracking.SearchAsync(AccessScope.Global, cancellationToken: TestContext.Current.CancellationToken));

        var bagService = new BagStorageService(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now));
        var bagCommand = new BagStoreCommand(
            "bag-key", "B1", "C1", "C2", Guid.NewGuid(), [Barcode], "seal", "road", Now, "corr");
        var bag = await bagService.StoreAsync(bagCommand, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(bag.IsReplay);
        database.ParcelLifecycle.IdempotencyRecords.Remove(
            await database.ParcelLifecycle.IdempotencyRecords.SingleAsync(
                record => record.Key == new IdempotencyKey("bag-key"),
                TestContext.Current.CancellationToken));
        await database.ParcelLifecycle.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True((await bagService.StoreAsync(bagCommand, cancellationToken: TestContext.Current.CancellationToken)).IsReplay);
        Assert.True((await bagService.StoreAsync(
            bagCommand with { CorrelationId = "retry-correlation" }, cancellationToken: TestContext.Current.CancellationToken)).IsReplay);
        await Assert.ThrowsAsync<CoreConflictException>(() => bagService.StoreAsync(
            bagCommand with { SealNumber = "changed" }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreConflictException>(() => bagService.StoreAsync(
            bagCommand with { BagBarcode = "B2" }, cancellationToken: TestContext.Current.CancellationToken));

        var dispatchService = new DispatchStorageService(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now));
        var dispatchCommand = new DispatchStoreCommand("dispatch-key", "D1", "C2", ["B1"], Now, "corr");
        await Assert.ThrowsAsync<CoreAuthorizationException>(() =>
            dispatchService.StoreAsync(dispatchCommand, new AccessScope(false, ["C2"]), cancellationToken: TestContext.Current.CancellationToken));
        Assert.False((await dispatchService.StoreAsync(dispatchCommand, new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken)).IsReplay);
        database.ParcelLifecycle.IdempotencyRecords.Remove(
            await database.ParcelLifecycle.IdempotencyRecords.SingleAsync(
                record => record.Key == new IdempotencyKey("dispatch-key"),
                TestContext.Current.CancellationToken));
        await database.ParcelLifecycle.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True((await dispatchService.StoreAsync(dispatchCommand, new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken)).IsReplay);
        Assert.True((await dispatchService.StoreAsync(
            dispatchCommand with { CorrelationId = "retry-correlation" },
            new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken)).IsReplay);
        await Assert.ThrowsAsync<CoreAuthorizationException>(() =>
            dispatchService.StoreAsync(dispatchCommand, new AccessScope(false, ["C2"]), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreConflictException>(() => dispatchService.StoreAsync(
            dispatchCommand with { ClosedAt = Now.AddMinutes(1) },
            new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreConflictException>(() => dispatchService.StoreAsync(
            dispatchCommand with { DispatchId = "D2" },
            new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken));

        var query = new BagDispatchQueryService(store);
        Assert.NotNull(await query.FindBagAsync("B1", new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await query.FindBagAsync("B1", new AccessScope(false, ["NO"]), cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await query.FindDispatchAsync("D1", new AccessScope(false, ["C2"]), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await query.FindDispatchAsync("D1", new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BagAndDispatchRejectMissingOrWrongDestinationMembership()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        await CreateInboundService(store).ExecuteAsync(
            InboundCommand("reading", Now, new PhysicalAttributesDto(1, 2, 3, 4)),
            cancellationToken: TestContext.Current.CancellationToken);
        var bagService = new BagStorageService(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now));
        await Assert.ThrowsAsync<CoreValidationException>(() => bagService.StoreAsync(new BagStoreCommand(
            "wrong-destination", "B1", "C1", "WRONG", Guid.NewGuid(), [Barcode], "seal", "road", Now, "corr"), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreValidationException>(() => bagService.StoreAsync(new BagStoreCommand(
            "missing-member", "B2", "C1", "C2", Guid.NewGuid(), ["999999999999999999999999"], "seal", "road", Now, "corr"), cancellationToken: TestContext.Current.CancellationToken));

        var dispatch = new DispatchStorageService(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now));
        await Assert.ThrowsAsync<CoreValidationException>(() => dispatch.StoreAsync(new DispatchStoreCommand(
            "missing-bag", "D1", "C2", ["MISSING"], Now, "corr"), AccessScope.Global, cancellationToken: TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<CoreAuthorizationException>(() => bagService.StoreAsync(new BagStoreCommand(
            "wrong-origin",
            "B3",
            "OTHER",
            "C2",
            Guid.NewGuid(),
            [Barcode],
            "seal",
            "road",
            Now,
            "corr"), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FinalEventAndBagRejectHistoricalCenterAfterLateOlderReadingWithoutSideEffects()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var destination = "DEST";
        await CreateInboundService(store, Now).ExecuteAsync(
            InboundCommand("owner-c1", Now, new PhysicalAttributesDto(1, 2, 3, 4)) with
            {
                DestinationCenterCode = destination,
                ExchangeCenterCode = "C1"
            },
            cancellationToken: TestContext.Current.CancellationToken);
        await CreateInboundService(store, Now.AddHours(2)).ExecuteAsync(
            InboundCommand("owner-c2", Now.AddHours(2), new PhysicalAttributesDto(1, 2, 3, 4)) with
            {
                DestinationCenterCode = destination,
                ExchangeCenterCode = "C2"
            },
            cancellationToken: TestContext.Current.CancellationToken);
        await CreateInboundService(store, Now.AddHours(3)).ExecuteAsync(
            InboundCommand("late-older-c1", Now.AddHours(1), new PhysicalAttributesDto(1, 2, 3, 4)) with
            {
                DestinationCenterCode = destination,
                ExchangeCenterCode = "C1"
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var dossier = await store.FindParcelAsync(
            new ParcelBarcode(Barcode),
            TestContext.Current.CancellationToken);
        Assert.Equal("C2", dossier?.LatestReading()?.ExchangeCenterCode.Value);
        var idempotencyCount = await database.ParcelLifecycle.IdempotencyRecords.CountAsync(TestContext.Current.CancellationToken);
        var inboxCount = await database.ParcelLifecycle.InboxMessages.CountAsync(TestContext.Current.CancellationToken);
        var outboxCount = await database.ParcelLifecycle.OutboxMessages.CountAsync(TestContext.Current.CancellationToken);
        var eventConsumer = new ParcelEventConsumer(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now.AddHours(4)));
        var final = new ParcelFinalEventCommand(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "C1",
            "historical-final",
            Barcode,
            CoreParcelStatus.Success,
            "corr",
            Now.AddHours(4));
        var bagService = new BagStorageService(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now.AddHours(4)));

        await Assert.ThrowsAsync<CoreAuthorizationException>(() =>
            eventConsumer.ConsumeAsync(final, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreAuthorizationException>(() => bagService.StoreAsync(
            new BagStoreCommand(
                "historical-bag",
                "HISTORICAL",
                "C1",
                destination,
                Guid.NewGuid(),
                [Barcode],
                "seal",
                "road",
                Now.AddHours(4),
                "corr"),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(idempotencyCount, await database.ParcelLifecycle.IdempotencyRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(inboxCount, await database.ParcelLifecycle.InboxMessages.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(outboxCount, await database.ParcelLifecycle.OutboxMessages.CountAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await database.ParcelLifecycle.Bags.ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Null(dossier?.InboundFinalStatus);
        Assert.Null(dossier?.PhysicalBagReference);
    }

    [Fact]
    public async Task LatestReadingOwnershipUsesReceiveTimeThenStableIdentifierForTies()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var timestamp = Now.AddHours(1);
        var destination = "DEST";
        var c1 = await CreateInboundService(store, Now.AddHours(2)).ExecuteAsync(
            InboundCommand("tie-c1", timestamp, new PhysicalAttributesDto(1, 2, 3, 4)) with
            {
                DestinationCenterCode = destination,
                ExchangeCenterCode = "C1"
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var c2 = await CreateInboundService(store, Now.AddHours(3)).ExecuteAsync(
            InboundCommand("tie-c2", timestamp, new PhysicalAttributesDto(1, 2, 3, 4)) with
            {
                DestinationCenterCode = destination,
                ExchangeCenterCode = "C2"
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var dossier = await store.FindParcelAsync(
            new ParcelBarcode(Barcode),
            TestContext.Current.CancellationToken);
        Assert.Equal(c2.ReadingRecordId, dossier?.LatestReading()?.Id);

        var c1SameReceipt = await CreateInboundService(store, Now.AddHours(3)).ExecuteAsync(
            InboundCommand("identifier-tie-c1", timestamp, new PhysicalAttributesDto(1, 2, 3, 4)) with
            {
                DestinationCenterCode = destination,
                ExchangeCenterCode = "C1"
            },
            cancellationToken: TestContext.Current.CancellationToken);
        var winningCenter = c1SameReceipt.ReadingRecordId.CompareTo(c2.ReadingRecordId) > 0 ? "C1" : "C2";
        var losingCenter = winningCenter == "C1" ? "C2" : "C1";
        var winningReadingId = winningCenter == "C1" ? c1SameReceipt.ReadingRecordId : c2.ReadingRecordId;
        Assert.NotEqual(Guid.Empty, c1.ReadingRecordId);
        Assert.Equal(winningReadingId, dossier?.LatestReading()?.Id);

        var eventConsumer = new ParcelEventConsumer(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now.AddHours(4)));
        await Assert.ThrowsAsync<CoreAuthorizationException>(() => eventConsumer.ConsumeAsync(
            new ParcelFinalEventCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                losingCenter,
                "identifier-tie-loser-final",
                Barcode,
                CoreParcelStatus.Success,
                "corr",
                Now.AddHours(4)),
            cancellationToken: TestContext.Current.CancellationToken));
        var bagService = new BagStorageService(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now.AddHours(4)));
        await Assert.ThrowsAsync<CoreAuthorizationException>(() => bagService.StoreAsync(
            new BagStoreCommand(
                "identifier-tie-loser-bag",
                "TIE-LOSER",
                losingCenter,
                destination,
                Guid.NewGuid(),
                [Barcode],
                "seal",
                "road",
                Now.AddHours(4),
                "corr"),
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(await eventConsumer.ConsumeAsync(
            new ParcelFinalEventCommand(
                Guid.NewGuid(),
                Guid.NewGuid(),
                winningCenter,
                "identifier-tie-winner-final",
                Barcode,
                CoreParcelStatus.Success,
                "corr",
                Now.AddHours(4)),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False((await bagService.StoreAsync(
            new BagStoreCommand(
                "identifier-tie-winner-bag",
                "TIE-WINNER",
                winningCenter,
                destination,
                Guid.NewGuid(),
                [Barcode],
                "seal",
                "road",
                Now.AddHours(4),
                "corr"),
            cancellationToken: TestContext.Current.CancellationToken)).IsReplay);
    }

    private static InboundQueryService CreateInboundService(
        EfParcelLifecycleStore store,
        DateTimeOffset? coreReceiveTime = null) => new(
        store,
        new BarcodeValidator(BarcodeValidationPolicy.Default),
        new DuplicateReadingClassifier(),
        new DiscrepancyChecker(),
        new CoreDeviceStatusMapper(),
        new CanonicalJsonRequestHasher(),
        new IdempotencyService(),
        new MutableTimeProvider(coreReceiveTime ?? Now),
        new OperationalSettings(
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(72),
            new DiscrepancyTolerance(1, 2)));

    private static InboundQueryCommand InboundCommand(
        string key,
        DateTimeOffset eventTime,
        PhysicalAttributesDto physicalAttributes) => new(
            key,
            [Barcode],
            "parcel",
            "express",
            "C1",
            "C2",
            Guid.NewGuid(),
            "C1",
            physicalAttributes,
            eventTime,
            "corr");
}
