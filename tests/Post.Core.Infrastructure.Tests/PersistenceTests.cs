using Microsoft.EntityFrameworkCore;
using Post.Core.Application.Common;
using Post.Core.Domain.Common;
using Post.Core.Domain.Devices;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Operations;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.Security;
using Post.Core.Domain.ValueObjects;
using Post.Core.Infrastructure.Persistence;
using Post.Core.Infrastructure.Testing;

namespace Post.Core.Infrastructure.Tests;

public sealed class PersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly SecretVerifier Verifier = new(
        Convert.ToBase64String(new byte[SecretVerifier.HashLength]),
        Convert.ToBase64String(new byte[SecretVerifier.SaltLength]),
        210_000);

    [Fact]
    public async Task SqliteFactoryCreatesFiveIndependentLogicalDatabases()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);

        Assert.True(await database.ParcelLifecycle.Database.CanConnectAsync(TestContext.Current.CancellationToken));
        Assert.True(await database.DeviceConfig.Database.CanConnectAsync(TestContext.Current.CancellationToken));
        Assert.True(await database.EdgeHealth.Database.CanConnectAsync(TestContext.Current.CancellationToken));
        Assert.True(await database.OperationalResult.Database.CanConnectAsync(TestContext.Current.CancellationToken));
        Assert.True(await database.Identity.Database.CanConnectAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ParcelStorePersistsAggregateGraphAndReliabilityRows()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        var parcel = ParcelDossier.Create(
            new ParcelBarcode("P1"), "parcel", "express",
            new ExchangeCenterCode("C1"), new ExchangeCenterCode("C2"));
        var reading = parcel.AppendReading(
            new IdempotencyKey("reading-key"), Now, DeviceId.New(), new ExchangeCenterCode("C1"),
            new PhysicalAttributes(1, 2, 3, 4), ReadingClassification.Success, Now, Now);
        reading.AddDiscrepancy(new PhysicalDiscrepancy(PhysicalAttributeKind.Weight, 1, 2));
        reading.AddAttachment("image.jpg", "image/jpeg", "parcel", Now);
        var bag = Bag.Create(
            new BagBarcode("B1"), new ExchangeCenterCode("C1"), new ExchangeCenterCode("C2"), DeviceId.New(),
            [parcel.Barcode], "seal", "road", Now, "bag-hash");
        var dispatch = Dispatch.Create(
            new DispatchId("D1"), new ExchangeCenterCode("C2"), [bag.Barcode], Now, "dispatch-hash");
        var eventId = Guid.NewGuid();
        var pendingEventId = Guid.NewGuid();
        var pending = PendingAttachment.Create(
            pendingEventId,
            "pending-hash",
            "{}",
            parcel.Barcode,
            Guid.NewGuid(),
            "pending.jpg",
            "image/jpeg",
            "parcel",
            "corr",
            Now,
            Now);
        store.AddParcel(parcel);
        store.AddBag(bag);
        store.AddDispatch(dispatch);
        store.AddPendingAttachment(pending);
        store.AddIdempotency(IdempotencyRecord.Start(new IdempotencyKey("request-key"), "hash", Now));
        store.AddInboxMessage(InboxMessage.Processed(eventId, "payload-hash", Now));
        store.AddOutbox(OutboxMessage.Create("topic", "{}", new CorrelationId("corr"), Now));
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.ParcelLifecycle.ChangeTracker.Clear();

        var loaded = await store.FindParcelAsync(parcel.Barcode, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(loaded);
        Assert.Single(loaded.Readings);
        Assert.Single(loaded.Readings.Single().Discrepancies);
        Assert.Single(loaded.Readings.Single().Attachments);
        Assert.Single(await database.ParcelLifecycle.DiscrepancyRecords.AsNoTracking()
            .ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await store.FindParcelsAsync([parcel.Barcode], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await store.QueryParcelsAsync([new ExchangeCenterCode("C1")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(await store.QueryParcelsAsync([new ExchangeCenterCode("NO")], cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await store.FindReadingAsync(reading.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await store.FindBagAsync(bag.Barcode, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await store.FindBagsAsync([bag.Barcode], cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await store.FindDispatchAsync(dispatch.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await store.FindIdempotencyAsync(new IdempotencyKey("request-key"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await store.FindInboxMessageAsync(eventId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await store.FindPendingAttachmentByEventAsync(pendingEventId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await store.GetDuePendingAttachmentsAsync(Now, 10, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.GetDuePendingAttachmentsAsync(Now, 0, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ParcelStoreTranslatesDatabaseUniquenessViolation()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfParcelLifecycleStore(database.ParcelLifecycle);
        store.AddIdempotency(IdempotencyRecord.Start(new IdempotencyKey("duplicate"), "hash", Now));
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.ParcelLifecycle.ChangeTracker.Clear();
        store.AddIdempotency(IdempotencyRecord.Start(new IdempotencyKey("duplicate"), "hash", Now));
        await Assert.ThrowsAsync<UniqueConstraintViolationException>(() => store.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeviceStorePersistsSafeVerifierSnapshotAndQueriesLatestVersion()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfDeviceConfigStore(database.DeviceConfig);
        var device = SortingDevice.Create(
            DeviceId.New(), "Sorter", "Sorter", new LogicalDeviceCode("L1"), "Post",
            new ExchangeCenterCode("C1"), string.Empty, Verifier);
        store.AddDevice(device);
        store.AddSnapshot(ConfigurationSnapshot.Create(
            new ExchangeCenterCode("C1"), new ConfigurationVersion(1), Now, [device],
            [new ExchangeCenterCode("100")], OperationalSettings.Default));
        store.AddSnapshot(ConfigurationSnapshot.Create(
            new ExchangeCenterCode("C1"), new ConfigurationVersion(2), Now.AddMinutes(1), [device],
            [new ExchangeCenterCode("101")], OperationalSettings.Default));
        store.AddOutbox(OutboxMessage.Create("config", "{}", new CorrelationId("corr"), Now));
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.DeviceConfig.ChangeTracker.Clear();

        Assert.NotNull(await store.FindDeviceAsync(device.Id, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await store.FindDeviceByLogicalCodeAsync(device.LogicalCode, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await store.ListDevicesAsync(new ExchangeCenterCode("C1"), cancellationToken: TestContext.Current.CancellationToken));
        var latest = await store.GetLatestSnapshotAsync(new ExchangeCenterCode("C1"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, latest?.Version.Value);
        Assert.Equal(Verifier.Hash, latest?.Devices.Single().SecretHash);
        Assert.Equal(Verifier.Salt, latest?.Devices.Single().SecretSalt);
    }

    [Fact]
    public async Task HealthOperationalAndIdentityStoresPersistTheirModels()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var healthStore = new EfEdgeHealthStore(database.EdgeHealth);
        var edge = EdgeHealthStatus.Register(EdgeId.New(), new ExchangeCenterCode("C1"), Now, "1.0", 1);
        healthStore.AddStatus(edge);
        healthStore.AddEvent(OperationalEvent.Create(
            OperationalEventType.Connected, edge.EdgeId, null, new CorrelationId("corr"), Now));
        healthStore.AddOutbox(OutboxMessage.Create("health", "{}", new CorrelationId("corr"), Now));
        await healthStore.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.EdgeHealth.ChangeTracker.Clear();
        Assert.NotNull(await healthStore.FindAsync(edge.EdgeId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await healthStore.ListAsync(TestContext.Current.CancellationToken));
        var persistedEvent = Assert.Single(
            await database.EdgeHealth.OperationalEvents.AsNoTracking()
                .ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(OperationalEventType.Connected, persistedEvent.EventType);

        var resultStore = new EfOperationalResultStore(database.OperationalResult);
        var result = OperationalResult.Create(
            EdgeId.New(), new ExchangeCenterCode("C1"),
            new IdempotencyKey("result-key"), "hash", new CorrelationId("corr"), null, "ok", true,
            null, null, Now, Now, 1, OperationalFinalStatus.Succeeded);
        resultStore.AddResult(result);
        resultStore.AddIdempotency(IdempotencyRecord.Start(new IdempotencyKey("result-idempotency"), "hash", Now));
        resultStore.AddOutbox(OutboxMessage.Create("result", "{}", new CorrelationId("corr"), Now));
        await resultStore.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.OperationalResult.ChangeTracker.Clear();
        var persistedResult = await resultStore.FindByIdempotencyKeyAsync(new IdempotencyKey("result-key"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(persistedResult);
        Assert.Equal(result.EdgeId, persistedResult.EdgeId);
        Assert.Equal("C1", persistedResult.ExchangeCenterCode.Value);

        var identityStore = new EfIdentityStore(database.Identity);
        var credential = EdgeCredential.Provision(
            DeviceId.New(), new LogicalDeviceCode("L1"), new ExchangeCenterCode("C1"), Verifier);
        identityStore.AddEdgeCredential(credential);
        identityStore.AddAudit(AuditRecord.Create(
            "admin", "create", "device", "1", null, new CorrelationId("corr"), Now));
        identityStore.AddOutbox(OutboxMessage.Create("identity", "{}", new CorrelationId("corr"), Now));
        await identityStore.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.Identity.ChangeTracker.Clear();
        Assert.NotNull(await identityStore.FindEdgeCredentialAsync(credential.DeviceId, cancellationToken: TestContext.Current.CancellationToken));
        Assert.NotNull(await identityStore.FindEdgeCredentialAsync(credential.LogicalDeviceCode, cancellationToken: TestContext.Current.CancellationToken));
        var persistedAudit = Assert.Single(await database.Identity.AuditRecords.AsNoTracking()
            .ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("create", persistedAudit.Action);
    }

    [Fact]
    public async Task StorePagingAppliesScopeFilterCountAndDeterministicOrderingInDatabase()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var parcelStore = new EfParcelLifecycleStore(database.ParcelLifecycle);
        foreach (var barcode in new[] { "P-003", "P-001", "P-002" })
        {
            parcelStore.AddParcel(ParcelDossier.Create(
                new ParcelBarcode(barcode),
                "parcel",
                "express",
                new ExchangeCenterCode("C1"),
                new ExchangeCenterCode("C2")));
        }

        await parcelStore.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.ParcelLifecycle.ChangeTracker.Clear();
        var parcelPage = await parcelStore.QueryParcelsPageAsync(
            [new ExchangeCenterCode("C1")],
            "P-00",
            skip: 1,
            take: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, parcelPage.TotalCount);
        Assert.Equal("P-002", Assert.Single(parcelPage.Items).Barcode.Value);
        Assert.Equal(0, (await parcelStore.QueryParcelsPageAsync(
            [new ExchangeCenterCode("NO")], null, 0, 10, cancellationToken: TestContext.Current.CancellationToken)).TotalCount);

        var deviceStore = new EfDeviceConfigStore(database.DeviceConfig);
        foreach (var (logical, center) in new[] { ("L3", "C2"), ("L2", "C1"), ("L1", "C1") })
        {
            deviceStore.AddDevice(SortingDevice.Create(
                DeviceId.New(), logical, "Sorter", new LogicalDeviceCode(logical), "Post",
                new ExchangeCenterCode(center), string.Empty, new SecretVerifier(
                    Verifier.Hash,
                    Verifier.Salt,
                    Verifier.Iterations,
                    Verifier.Algorithm)));
        }

        await deviceStore.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.DeviceConfig.ChangeTracker.Clear();
        var devicePage = await deviceStore.QueryDevicesPageAsync(
            [new ExchangeCenterCode("C1")],
            skip: 1,
            take: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, devicePage.TotalCount);
        Assert.Equal("L2", Assert.Single(devicePage.Items).LogicalCode.Value);

        var healthStore = new EfEdgeHealthStore(database.EdgeHealth);
        healthStore.AddStatus(EdgeHealthStatus.Register(
            EdgeId.New(), new ExchangeCenterCode("C2"), Now, "1", 1));
        healthStore.AddStatus(EdgeHealthStatus.Register(
            EdgeId.New(), new ExchangeCenterCode("C1"), Now, "1", 1));
        await healthStore.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.EdgeHealth.ChangeTracker.Clear();
        var healthPage = await healthStore.QueryPageAsync(
            [new ExchangeCenterCode("C1")],
            skip: 0,
            take: 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, healthPage.TotalCount);
        Assert.Equal("C1", Assert.Single(healthPage.Items).ExchangeCenterCode.Value);
    }

    [Fact]
    public async Task CompositeOutboxOrdersPendingMessagesAcrossAllFiveDatabases()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        database.ParcelLifecycle.OutboxMessages.Add(OutboxMessage.Create(
            "p", "{}", new CorrelationId("corr"), Now, 5));
        database.DeviceConfig.OutboxMessages.Add(OutboxMessage.Create(
            "d", "{}", new CorrelationId("corr"), Now, 1));
        database.EdgeHealth.OutboxMessages.Add(OutboxMessage.Create(
            "h", "{}", new CorrelationId("corr"), Now, 3));
        var store = new CompositeEfOutboxStore(
            database.ParcelLifecycle,
            database.DeviceConfig,
            database.EdgeHealth,
            database.OperationalResult,
            database.Identity);
        await store.SaveChangesAsync(TestContext.Current.CancellationToken);
        database.ParcelLifecycle.ChangeTracker.Clear();
        database.DeviceConfig.ChangeTracker.Clear();
        database.EdgeHealth.ChangeTracker.Clear();
        database.OperationalResult.ChangeTracker.Clear();
        database.Identity.ChangeTracker.Clear();

        var singleStore = new EfSingleDatabaseOutboxStore(
            database.ParcelLifecycle,
            database.ParcelLifecycle.OutboxMessages);
        Assert.Single(await singleStore.GetPendingAsync(10, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => singleStore.GetPendingAsync(0, cancellationToken: TestContext.Current.CancellationToken));
        await singleStore.SaveChangesAsync(TestContext.Current.CancellationToken);

        var pending = await store.GetPendingAsync(2, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([1L, 3L], pending.Select(x => x.Sequence));
    }
}
