using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Application.Services;
using Post.Edge.Domain.Bags;
using Post.Edge.Domain.Devices;
using Post.Edge.Domain.Dispatches;
using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;
using Post.Edge.Infrastructure.Concurrency;
using Post.Edge.Infrastructure.Integrations.Mock;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Persistence;
using Post.Edge.Infrastructure.Workers;

namespace Post.Edge.Infrastructure.Tests;

public sealed class PersistenceAndWorkerTests
{
    [Fact]
    public async Task SqliteMigrationPersistsAggregateChangesAndSupportsWal()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        var readId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var parcel = EdgeParcel.Create(
                readId,
                BarcodeSet.Create(["123450000000005954400001"]),
                "D1",
                DateTimeOffset.UtcNow);
            parcel.AssignDestination(PostalCenterCode.Create("123"), "C1");
            scope.ServiceProvider.GetRequiredService<IEdgeParcelRepository>().Add(parcel);
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var parcel = await scope.ServiceProvider.GetRequiredService<IEdgeParcelRepository>()
                .FindByReadIdAsync(readId, TestContext.Current.CancellationToken);
            parcel!.MarkCoreSynced();
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
            await db.Database.OpenConnectionAsync(cancellationToken: TestContext.Current.CancellationToken);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            var journalMode = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            var parcel = await scope.ServiceProvider.GetRequiredService<IEdgeParcelRepository>()
                .FindByReadIdAsync(readId, TestContext.Current.CancellationToken);
            Assert.Equal("wal", Assert.IsType<string>(journalMode), ignoreCase: true);
            Assert.True(parcel!.CoreSynced);
        }
    }

    [Fact]
    public async Task OperationJournalStoresAndUpdatesTypedIdempotentResponse()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        var operationId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
            journal.AddResponse(operationId, "test", new JournalResponse(1), DateTimeOffset.UtcNow, "HASH-1");
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
            Assert.Equal(1, (await journal.GetResponseAsync<JournalResponse>(operationId, "test", TestContext.Current.CancellationToken))!.Value);
            journal.AddResponse(operationId, "test", new JournalResponse(2), DateTimeOffset.UtcNow);
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, (await journal.GetResponseAsync<JournalResponse>(operationId, "test", TestContext.Current.CancellationToken))!.Value);
        }
    }

    [Fact]
    public async Task OutboxProcessorPublishesCoreMessageExactlyOnce()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IOutboxWriter>().Enqueue(new OutboxMessage(
                Guid.NewGuid(), "Core", "TestEvent", "{}", "test:1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
            var first = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(cancellationToken: TestContext.Current.CancellationToken);
            var second = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(1, first.Completed);
            Assert.Equal(0, second.Claimed);
        }
        var core = Assert.IsType<MockCoreApiClient>(host.Services.GetRequiredService<ICoreApiClient>());
        Assert.Single(core.Published);
    }

    [Fact]
    public async Task OutboxProcessorMovesTerminalFailureToDeadLetterAndCanRequeue()
    {
        await using var host = await InfrastructureTestHost.CreateAsync(
            new Dictionary<string, string?> { ["Edge:Workers:MaxOutboxAttempts"] = "1" },
            services => services.AddSingleton<ICoreApiClient, FailingCoreClient>());
        var messageId = Guid.NewGuid();
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IOutboxWriter>().Enqueue(new OutboxMessage(
            messageId, "Core", "TestEvent", "{}", "test:failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(cancellationToken: TestContext.Current.CancellationToken);
        var deadLetters = scope.ServiceProvider.GetRequiredService<DeadLetterService>();

        Assert.Equal(1, result.DeadLettered);
        Assert.Equal(1, await deadLetters.CountAsync(TestContext.Current.CancellationToken));
        var persistedFailure = await scope.ServiceProvider.GetRequiredService<EdgeDbContext>()
            .DeadLetters.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("HTTP_DEPENDENCY_FAILURE", persistedFailure.Error);
        Assert.DoesNotContain("offline", persistedFailure.Error, StringComparison.Ordinal);
        Assert.True(await deadLetters.RequeueAsync(messageId, TestContext.Current.CancellationToken));
        Assert.Equal(0, await deadLetters.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LocalNetworkBindingsAreNormalizedReplacedAndPersisted()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IDeviceNetworkBindingRepository>();

        await repository.ReplaceAsync("D1", ["127.0.0.1", "::ffff:127.0.0.1"], TestContext.Current.CancellationToken);
        await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        var values = await repository.ListAsync("D1", TestContext.Current.CancellationToken);

        Assert.Single(values);
        Assert.True(values[0].Matches("127.0.0.1"));
    }

    [Fact]
    public async Task PendingInboundRepositoryUpdatesLoadedItemAndSupportsSynchronousCommit()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        var operationId = Guid.NewGuid();
        var readId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPendingInboundRepository>();
            repository.Add(new PendingInboundItem(
                operationId, readId, "tracking-1", 0, DateTimeOffset.UtcNow, null));
            var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
            Assert.Equal(1, db.SaveChanges(acceptAllChangesOnSuccess: true));

            var loaded = await repository.FindByOperationIdAsync(operationId, TestContext.Current.CancellationToken);
            repository.Update(loaded! with { Attempts = 2, LastError = "retry" });
            Assert.Equal(1, db.SaveChanges(acceptAllChangesOnSuccess: true));
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<IPendingInboundRepository>()
                .FindByOperationIdAsync(operationId, TestContext.Current.CancellationToken);
            Assert.Equal(2, loaded!.Attempts);
            Assert.Equal("retry", loaded.LastError);
        }
    }

    [Fact]
    public async Task MockBoundariesReturnDeterministicParcelInfoAndCaptureDeviceEvent()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        var parcelInfo = await host.Services.GetRequiredService<IParcelInfoClient>()
            .GetAsync("59544", "123", 1, TestContext.Current.CancellationToken);
        var publisher = Assert.IsType<InMemoryDeviceEventPublisher>(
            host.Services.GetRequiredService<IDeviceEventPublisher>());
        var core = Assert.IsType<MockCoreApiClient>(
            host.Services.GetRequiredService<ICoreApiClient>());
        var edgeId = Guid.NewGuid();

        await publisher.PublishAsync(new DeviceEvent("ready", "{}", "event:1"), TestContext.Current.CancellationToken);
        await core.RegisterAsync(
            new CoreEdgeRegistration(edgeId, "59544", "test", 1, DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);
        await core.SendHeartbeatAsync(
            new CoreEdgeHeartbeat(
                edgeId,
                DateTimeOffset.UnixEpoch,
                "test",
                1,
                1_000,
                0,
                0,
                0,
                DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);

        Assert.Equal(100, parcelInfo!.Weight);
        Assert.Equal("MOCK", parcelInfo.BoxSize);
        Assert.Equal("event:1", Assert.Single(publisher.Messages).IdempotencyKey);
    }

    [Fact]
    public async Task DesignTimeFactoryCreatesSqliteContextWithoutOpeningAConnection()
    {
        var factory = new EdgeDesignTimeDbContextFactory();

        await using var context = factory.CreateDbContext([]);

        Assert.IsType<EdgeDbContext>(context);
        Assert.Contains("edge.design.db", context.Database.GetConnectionString(), StringComparison.Ordinal);
        Assert.False(context.Database.GetDbConnection().State == System.Data.ConnectionState.Open);
    }

    [Fact]
    public async Task BagPendingClosureAndDispatchRepositoriesResolveByOperationId()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        var bag = CreateBag("BAG-BY-OPERATION", "P-BY-OPERATION", DateTimeOffset.UnixEpoch);
        var closure = PendingBagClosure.Create(
            Guid.NewGuid(),
            "D1",
            PostalCenterCode.Create("123"),
            [Guid.NewGuid()],
            "SEAL-1",
            "Road",
            DateTimeOffset.UnixEpoch);
        var dispatch = DispatchRecord.Create(
            Guid.NewGuid(),
            "DISPATCH-BY-OPERATION",
            "D1",
            "00123",
            [bag.BagBarcode],
            DateTimeOffset.UnixEpoch);
        await using var scope = host.Services.CreateAsyncScope();
        var bags = scope.ServiceProvider.GetRequiredService<IEdgeBagRepository>();
        var pending = scope.ServiceProvider.GetRequiredService<IPendingBagClosureRepository>();
        var dispatches = scope.ServiceProvider.GetRequiredService<IDispatchRepository>();
        bags.Add(bag);
        pending.Add(closure);
        dispatches.Add(dispatch);
        await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(await bags.FindByOperationIdAsync(
            bag.LocalOperationId,
            TestContext.Current.CancellationToken));
        Assert.NotNull(await pending.FindByOperationIdAsync(
            closure.LocalOperationId,
            TestContext.Current.CancellationToken));
        Assert.NotNull(await dispatches.FindByOperationIdAsync(
            dispatch.LocalOperationId,
            TestContext.Current.CancellationToken));
        Assert.Null(await bags.FindByOperationIdAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken));
        Assert.Null(await pending.FindByOperationIdAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken));
        Assert.Null(await dispatches.FindByOperationIdAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DatabaseInitializerStopIsNonBlocking()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        var initializer = new EdgeDatabaseInitializer(
            host.Services,
            Microsoft.Extensions.Options.Options.Create(new EdgeStorageOptions()));

        var stop = initializer.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(stop.IsCompletedSuccessfully);
        await stop;
    }

    [Fact]
    public async Task DatabaseInitializerClearsCrashStaleSessionsOnStartup()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDeviceSessionStore>().SaveAsync(DeviceSession.Connect(Guid.NewGuid(), "stale-connection", "127.0.0.1", DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        }
        var initializer = new EdgeDatabaseInitializer(
            host.Services,
            Microsoft.Extensions.Options.Options.Create(new EdgeStorageOptions
            {
                ApplyMigrationsOnStartup = false
            }),
            new FakeHostEnvironment("Testing"));

        await initializer.StartAsync(TestContext.Current.CancellationToken);

        await using var verificationScope = host.Services.CreateAsyncScope();
        Assert.Null(await verificationScope.ServiceProvider.GetRequiredService<IDeviceSessionStore>()
            .FindByConnectionAsync("stale-connection", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DatabaseInitializerFailsClosedWhenProductionMigrationsAreDisabled()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        var initializer = new EdgeDatabaseInitializer(
            host.Services,
            Microsoft.Extensions.Options.Options.Create(new EdgeStorageOptions
            {
                ApplyMigrationsOnStartup = false
            }),
            new FakeHostEnvironment(Environments.Production));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("only in Development or Testing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DestinationLockSerializesSameDeviceAndDestination()
    {
        var provider = new InMemoryDestinationLockProvider();
        await using var first = await provider.AcquireAsync("D1", "00123", TestContext.Current.CancellationToken);
        var secondTask = provider.AcquireAsync("D1", "00123", TestContext.Current.CancellationToken).AsTask();
        Assert.False(secondTask.IsCompleted);
        await first.DisposeAsync();
        await using var second = await secondTask;
        Assert.NotNull(second);
    }

    [Fact]
    public void LabelGeneratorProducesBase64PayloadWithCounters()
    {
        var generator = new BasicBagLabelGenerator();
        var label = generator.Generate(
            "BAG-1", "D1", "00123", new BagCounts(2, 1, 1, 0), DateTimeOffset.UnixEpoch);

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(label));

        Assert.Contains("BAG=BAG-1", decoded, StringComparison.Ordinal);
        Assert.Contains("N=2;P=1;M=1;Q=0", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EdgeReadinessRequiresStorageAndPersistedConfiguration()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        var health = host.Services.GetRequiredService<HealthCheckService>();

        var unavailable = await health.CheckHealthAsync(registration => registration.Name == EdgeReadinessHealthCheck.RegistrationName, TestContext.Current.CancellationToken);
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IConfigurationSyncService>().SyncAsync(TestContext.Current.CancellationToken);
        var ready = await health.CheckHealthAsync(registration => registration.Name == EdgeReadinessHealthCheck.RegistrationName, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, unavailable.Status);
        Assert.Equal(HealthStatus.Healthy, ready.Status);
        Assert.DoesNotContain("Data Source", string.Join(' ', ready.Entries.Values.Select(entry => entry.Description)),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EdgeReadinessFailsSafelyBelowDiskThresholdAndRealProbeResolvesStorageDrive()
    {
        await using var host = await InfrastructureTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IDiskSpaceProbe>();
            services.AddSingleton<IDiskSpaceProbe>(new StaticDiskSpaceProbe(1));
        });
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IConfigurationSyncService>().SyncAsync(TestContext.Current.CancellationToken);

        var health = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(registration => registration.Name == EdgeReadinessHealthCheck.RegistrationName, TestContext.Current.CancellationToken);
        var realProbe = new EdgeDiskSpaceProbe(Microsoft.Extensions.Options.Options.Create(new EdgeStorageOptions
        {
            ConnectionString = $"Data Source={host.DatabasePath}"
        }));

        Assert.Equal(HealthStatus.Unhealthy, health.Status);
        Assert.Contains("disk", Assert.Single(health.Entries).Value.Description, StringComparison.OrdinalIgnoreCase);
        Assert.True(realProbe.GetAvailableBytes() > 0);
    }

    [Fact]
    public async Task ConfigurationPollingReplacesNewerSnapshotAndStopsOnCancellation()
    {
        var source = new RecordingConfigurationSource();
        await using var host = await InfrastructureTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IConfigurationSource>();
            services.AddSingleton<IConfigurationSource>(source);
        });
        var worker = new ConfigurationPollingWorker(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(
                new EdgeWorkerOptions { ConfigurationSyncInterval = TimeSpan.FromMilliseconds(10) }),
            NullLogger<ConfigurationPollingWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await source.Fetched.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var pollingScope = host.Services.CreateAsyncScope();
            if (await pollingScope.ServiceProvider.GetRequiredService<IDeviceConfigurationRepository>()
                    .GetAsync(TestContext.Current.CancellationToken) is not null)
                break;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await worker.StopAsync(TestContext.Current.CancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IDeviceConfigurationRepository>().GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, snapshot!.Version);
    }

    [Fact]
    public async Task CoreHealthWorkerRegistersThenPublishesHeartbeatAndStopsCleanly()
    {
        var reporter = new RecordingCoreHealthClient();
        var edgeId = Guid.NewGuid();
        await using var host = await InfrastructureTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<ICoreEdgeHealthClient>();
            services.AddSingleton<ICoreEdgeHealthClient>(reporter);
        });
        await using (var scope = host.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IConfigurationSyncService>().SyncAsync(TestContext.Current.CancellationToken);
        var worker = new CoreEdgeHealthWorker(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(
                new EdgeWorkerOptions { HeartbeatInterval = TimeSpan.FromSeconds(5) }),
            Microsoft.Extensions.Options.Options.Create(
                new CoreApiOptions { EdgeId = edgeId, ExchangeCenterCode = "59544" }),
            host.Services.GetRequiredService<IClock>(),
            new StaticDiskSpaceProbe(9_999_999),
            NullLogger<CoreEdgeHealthWorker>.Instance);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        await reporter.HeartbeatSent.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await worker.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(edgeId, Assert.Single(reporter.Registrations).EdgeId);
        var heartbeat = Assert.Single(reporter.Heartbeats);
        Assert.Equal(1, heartbeat.ConfigurationVersion);
        Assert.Equal(9_999_999, heartbeat.AvailableDiskBytes);
    }

    [Fact]
    public async Task AutoDispatchRunsAgainstSqliteAndUsesStableCurrentBagSet()
    {
        await using var host = await InfrastructureTestHost.CreateAsync(new Dictionary<string, string?>
        {
            ["Edge:AutoDispatch:Enabled"] = "true",
            ["Edge:AutoDispatch:MinimumBagCount"] = "1"
        });
        await using (var configurationScope = host.Services.CreateAsyncScope())
            await configurationScope.ServiceProvider.GetRequiredService<IConfigurationSyncService>().SyncAsync(TestContext.Current.CancellationToken);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IEdgeBagRepository>().Add(CreateBag(
                "BAG-AUTO", "P-AUTO", DateTimeOffset.UtcNow.AddHours(-3)));
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await RunScopedAsync<AutoDispatchService, AutoDispatchRunResult>(
            host.Services, service => service.RunAsync());

        Assert.Equal(1, result.CandidateGroups);
        Assert.Equal(1, result.Completed);
        await using var verification = host.Services.CreateAsyncScope();
        Assert.True((await verification.ServiceProvider.GetRequiredService<IEdgeBagRepository>()
            .FindByBarcodeAsync("BAG-AUTO", TestContext.Current.CancellationToken))!.DispatchAssigned);
    }

    [Fact]
    public async Task CleanupIsCancellationSafeAndDeletesOnlyConfirmedDispatchChains()
    {
        var now = new DateTimeOffset(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);
        await using var host = await InfrastructureTestHost.CreateAsync(
            new Dictionary<string, string?>
            {
                ["Edge:Workers:CleanupEnabled"] = "true",
                ["Edge:Workers:RetentionPeriod"] = "1.00:00:00",
                ["Edge:Workers:IdempotencyRetentionPeriod"] = "1.00:00:00"
            },
            services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new StaticClock(now));
            });
        var terminalOperationId = Guid.NewGuid();
        var activeOperationId = Guid.NewGuid();
        var unknownOperationId = Guid.NewGuid();
        var orphanedInboundOperationId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var old = now.AddDays(-2);
            var parcel = EdgeParcel.Create(
                Guid.NewGuid(), BarcodeSet.Create(["123450000000005954400001"]), "D1", old);
            parcel.AssignDestination(PostalCenterCode.Create("123"), "C1");
            parcel.MarkExportRegistered();
            parcel.MarkBagged("BAG-CONFIRMED");
            parcel.MarkCoreSynced();
            scope.ServiceProvider.GetRequiredService<IEdgeParcelRepository>().Add(parcel);
            var bag = CreateBag("BAG-CONFIRMED", parcel.CanonicalBarcode, old);
            bag.AssignDispatch("DISPATCH-CONFIRMED");
            bag.MarkCoreConfirmed();
            scope.ServiceProvider.GetRequiredService<IEdgeBagRepository>().Add(bag);
            var dispatch = DispatchRecord.Create(
                Guid.NewGuid(), "DISPATCH-CONFIRMED", "D1", "00123", [bag.BagBarcode], old);
            dispatch.MarkCoreConfirmed();
            scope.ServiceProvider.GetRequiredService<IDispatchRepository>().Add(dispatch);
            scope.ServiceProvider.GetRequiredService<IEdgeBagRepository>().Add(
                CreateBag("BAG-UNCONFIRMED", "P-UNCONFIRMED", old));

            var partialParcel = EdgeParcel.Create(
                Guid.NewGuid(), BarcodeSet.Create(["123450000000005954400002"]), "D1", old);
            partialParcel.AssignDestination(PostalCenterCode.Create("123"), "C1");
            partialParcel.MarkExportRegistered();
            partialParcel.MarkBagged("BAG-PARTIAL");
            scope.ServiceProvider.GetRequiredService<IEdgeParcelRepository>().Add(partialParcel);
            var partialBag = CreateBag("BAG-PARTIAL", partialParcel.CanonicalBarcode, old);
            partialBag.AssignDispatch("DISPATCH-PARTIAL");
            partialBag.MarkCoreConfirmed();
            scope.ServiceProvider.GetRequiredService<IEdgeBagRepository>().Add(partialBag);
            var partialDispatch = DispatchRecord.Create(
                Guid.NewGuid(), "DISPATCH-PARTIAL", "D1", "00123", [partialBag.BagBarcode], old);
            partialDispatch.MarkCoreConfirmed();
            scope.ServiceProvider.GetRequiredService<IDispatchRepository>().Add(partialDispatch);

            var terminalClosure = PendingBagClosure.Create(
                terminalOperationId,
                "D1",
                PostalCenterCode.Create("123"),
                [Guid.NewGuid()],
                "SEAL-T",
                "Road",
                old);
            terminalClosure.MarkPermanentlyFailed("terminal");
            scope.ServiceProvider.GetRequiredService<IPendingBagClosureRepository>().Add(terminalClosure);
            scope.ServiceProvider.GetRequiredService<IPendingBagClosureRepository>().Add(PendingBagClosure.Create(
                activeOperationId,
                "D1",
                PostalCenterCode.Create("123"),
                [Guid.NewGuid()],
                "SEAL-A",
                "Road",
                old));
            var journal = scope.ServiceProvider.GetRequiredService<IOperationJournal>();
            journal.AddResponse(
                terminalOperationId,
                "bag.close",
                CleanupBagError(terminalOperationId),
                old,
                "TERMINAL-HASH");
            journal.AddResponse(
                activeOperationId,
                "bag.close",
                CleanupBagError(activeOperationId),
                old,
                "ACTIVE-HASH");
            journal.AddResponse(
                unknownOperationId,
                "unknown.operation",
                new JournalResponse(1),
                old,
                "UNKNOWN-HASH");
            journal.AddResponse(
                orphanedInboundOperationId,
                "parcel.inbound",
                new InboundParcelResponse(
                    Guid.NewGuid(),
                    orphanedInboundOperationId,
                    Guid.NewGuid(),
                    InboundResultStatus.Registered,
                    "123450000000005954400099",
                    "59544",
                    "59544"),
                old,
                "ORPHANED-INBOUND-HASH");
            var externalOperations = scope.ServiceProvider
                .GetRequiredService<IExternalOperationSagaRepository>();
            externalOperations.AddIntent(new ExternalOperationSaga(
                Guid.NewGuid(),
                "bag.close",
                "FINALIZED-HASH",
                "{}",
                "{}",
                true,
                old,
                old));
            externalOperations.AddIntent(new ExternalOperationSaga(
                Guid.NewGuid(),
                "bag.close",
                "UNRESOLVED-HASH",
                "{}",
                null,
                false,
                old,
                old));
            scope.ServiceProvider.GetRequiredService<IOutboxWriter>().Enqueue(new OutboxMessage(
                Guid.NewGuid(),
                "DeadLetter",
                "RetainedFailure",
                "{}",
                "cleanup:dead-letter",
                old,
                old));
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using (var source = new CancellationTokenSource())
        {
            source.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RunScopedAsync<LocalCleanupService, LocalCleanupResult>(
                    host.Services, service => service.RunAsync(source.Token)));
        }
        var result = await RunScopedAsync<LocalCleanupService, LocalCleanupResult>(
            host.Services, service => service.RunAsync());

        Assert.Equal(1, result.Parcels);
        Assert.Equal(1, result.Bags);
        Assert.Equal(1, result.Dispatches);
        Assert.Equal(1, result.TerminalBagClosures);
        Assert.Equal(2, result.OperationJournalEntries);
        Assert.Equal(1, result.FinalizedExternalOperations);
        await using var verification = host.Services.CreateAsyncScope();
        Assert.NotNull(await verification.ServiceProvider.GetRequiredService<IEdgeBagRepository>()
            .FindByBarcodeAsync("BAG-UNCONFIRMED", TestContext.Current.CancellationToken));
        Assert.NotNull(await verification.ServiceProvider.GetRequiredService<IEdgeBagRepository>()
            .FindByBarcodeAsync("BAG-PARTIAL", TestContext.Current.CancellationToken));
        Assert.Equal(1, await verification.ServiceProvider.GetRequiredService<DeadLetterService>().CountAsync(TestContext.Current.CancellationToken));
        Assert.Single(await verification.ServiceProvider.GetRequiredService<IPendingBagClosureRepository>()
            .ListDueAsync(now, 10, TestContext.Current.CancellationToken));
        var journalVerification = verification.ServiceProvider.GetRequiredService<IOperationJournal>();
        Assert.Null(await journalVerification.GetResponseAsync<CloseBagResponse>(terminalOperationId, "bag.close", TestContext.Current.CancellationToken));
        Assert.NotNull(await journalVerification.GetResponseAsync<CloseBagResponse>(activeOperationId, "bag.close", TestContext.Current.CancellationToken));
        Assert.NotNull(await journalVerification.GetResponseAsync<JournalResponse>(unknownOperationId, "unknown.operation", TestContext.Current.CancellationToken));
        Assert.Null(await journalVerification.GetResponseAsync<InboundParcelResponse>(
            orphanedInboundOperationId,
            "parcel.inbound",
            TestContext.Current.CancellationToken));
        var externalStatus = await verification.ServiceProvider
            .GetRequiredService<IExternalOperationSagaRepository>().GetStatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, externalStatus.UnresolvedCount);
    }

    [Fact]
    public async Task CleanupIsOptInAndDoesNotDeleteWhenDisabled()
    {
        await using var host = await InfrastructureTestHost.CreateAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IEdgeBagRepository>().Add(
                CreateBag("BAG-RETAINED", "P-RETAINED", DateTimeOffset.UnixEpoch));
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await RunScopedAsync<LocalCleanupService, LocalCleanupResult>(
            host.Services,
            service => service.RunAsync());

        Assert.Equal(new LocalCleanupResult(0, 0, 0, 0, 0, 0, 0), result);
        await using var verification = host.Services.CreateAsyncScope();
        Assert.NotNull(await verification.ServiceProvider.GetRequiredService<IEdgeBagRepository>()
            .FindByBarcodeAsync("BAG-RETAINED", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BackgroundWorkersRedactRawExceptionsFromStructuredLogs()
    {
        var provider = new ThrowingScopeServiceProvider();
        var scopeFactory = Assert.IsAssignableFrom<IServiceScopeFactory>(
            provider.GetService(typeof(IServiceScopeFactory)));
        var workers = Microsoft.Extensions.Options.Options.Create(new EdgeWorkerOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            ConfigurationSyncInterval = TimeSpan.FromMilliseconds(10),
            HeartbeatInterval = TimeSpan.FromMilliseconds(10),
            CleanupEnabled = true,
            CleanupInterval = TimeSpan.FromMilliseconds(10)
        });
        var autoDispatch = Microsoft.Extensions.Options.Options.Create(new EdgeAutoDispatchOptions
        {
            Enabled = true,
            ScanInterval = TimeSpan.FromMilliseconds(10)
        });

        await VerifySafeWorkerLogAsync(
            new OutboxWorker(
                provider,
                workers,
                new OutboxWakeSignal(),
                new CapturingLogger<OutboxWorker>()));
        await VerifySafeWorkerLogAsync(
            new PendingWorkWorker(provider, workers, new CapturingLogger<PendingWorkWorker>()));
        await VerifySafeWorkerLogAsync(
            new ConfigurationPollingWorker(
                scopeFactory,
                workers,
                new CapturingLogger<ConfigurationPollingWorker>()));
        await VerifySafeWorkerLogAsync(
            new CoreEdgeHealthWorker(
                scopeFactory,
                workers,
                Microsoft.Extensions.Options.Options.Create(new CoreApiOptions()),
                new StaticClock(DateTimeOffset.UnixEpoch),
                new StaticDiskSpaceProbe(1),
                new CapturingLogger<CoreEdgeHealthWorker>()));
        await VerifySafeWorkerLogAsync(
            new AutoDispatchWorker(
                scopeFactory,
                autoDispatch,
                new CapturingLogger<AutoDispatchWorker>()));
        await VerifySafeWorkerLogAsync(
            new LocalCleanupWorker(
                scopeFactory,
                workers,
                new CapturingLogger<LocalCleanupWorker>()));
    }

    private static EdgeBag CreateBag(string bagBarcode, string parcelBarcode, DateTimeOffset createdAt) =>
        EdgeBag.Create(
            bagBarcode,
            Guid.NewGuid(),
            "D1",
            PostalCenterCode.Create("123"),
            [parcelBarcode],
            [1],
            "SEAL",
            "Road",
            new BagCounts(1, 0, 0, 0),
            null,
            createdAt);

    private static CloseBagResponse CleanupBagError(Guid operationId) => new(
        operationId,
        operationId,
        BagCloseResult.Error,
        null,
        null,
        new BagCounts(0, 0, 0, 0),
        [],
        "TERMINAL",
        "The operation failed.");

    private static async Task<TResult> RunScopedAsync<TService, TResult>(
        IServiceProvider provider,
        Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private static async Task VerifySafeWorkerLogAsync<TWorker>(TWorker worker)
        where TWorker : IHostedService
    {
        var logger = CapturingLogger<TWorker>.LastCreated
                     ?? throw new InvalidOperationException("The test logger was not constructed.");
        await worker.StartAsync(default);
        await logger.Logged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(default);

        Assert.Null(logger.Exception);
        Assert.DoesNotContain(ThrowingScopeServiceProvider.SensitiveFailure, logger.Message, StringComparison.Ordinal);
    }

    private sealed record JournalResponse(int Value);

    private sealed class FailingCoreClient : ICoreApiClient
    {
        public Task<CoreInboundLookupResult> LookupInboundAsync(
            CoreInboundLookupRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CoreInboundLookupResult(CoreParcelDisposition.Normal, null, null));

        public Task PublishAsync(
            string messageType,
            string payloadJson,
            string idempotencyKey,
            CancellationToken cancellationToken) =>
            Task.FromException(new HttpRequestException("offline"));
    }

    private sealed class FakeHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Post.Edge.Infrastructure.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingCoreHealthClient : ICoreEdgeHealthClient
    {
        internal List<CoreEdgeRegistration> Registrations { get; } = [];
        internal List<CoreEdgeHeartbeat> Heartbeats { get; } = [];
        internal TaskCompletionSource<bool> HeartbeatSent { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RegisterAsync(CoreEdgeRegistration registration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Registrations.Add(registration);
            return Task.CompletedTask;
        }

        public Task SendHeartbeatAsync(CoreEdgeHeartbeat heartbeat, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Heartbeats.Add(heartbeat);
            HeartbeatSent.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class StaticDiskSpaceProbe(long availableBytes) : IDiskSpaceProbe
    {
        public long GetAvailableBytes() => availableBytes;
    }

    private sealed class RecordingConfigurationSource : IConfigurationSource
    {
        internal TaskCompletionSource<bool> Fetched { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<EdgeConfigurationDto?> FetchAsync(long? currentVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verifier = DeviceSecretVerifier.DeriveForDevelopment("test-token");
            Fetched.TrySetResult(true);
            return Task.FromResult<EdgeConfigurationDto?>(new EdgeConfigurationDto(
                "59544",
                5,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromHours(1),
                [new DeviceConfigurationDto(
                    "D1", "SORT-1", true, verifier.Algorithm, verifier.Iterations,
                    verifier.SaltBase64, verifier.HashBase64)],
                ["59544"]));
        }
    }

    private sealed class StaticClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class ThrowingScopeServiceProvider : IServiceProvider, IServiceScopeFactory
    {
        internal const string SensitiveFailure = "sensitive-url-secret-body";

        public object? GetService(Type serviceType) => serviceType == typeof(IServiceScopeFactory) ? this : null;

        public IServiceScope CreateScope() => throw new InvalidOperationException(SensitiveFailure);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal CapturingLogger()
        {
            LastCreated = this;
        }

        internal static CapturingLogger<T>? LastCreated { get; private set; }
        internal TaskCompletionSource<bool> Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Message { get; private set; } = string.Empty;
        internal Exception? Exception { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Message = formatter(state, exception);
            Exception = exception;
            Logged.TrySetResult(true);
        }
    }
}
