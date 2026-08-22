using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Post.Core.Application.Abstractions;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.ValueObjects;
using Post.Core.Infrastructure;
using Post.Core.Infrastructure.Persistence;

namespace Post.Core.Caching.Tests;

public sealed class RedisMirroringStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ParcelLifecycleStoreMirrorsOnlyAfterSqlCommitAndAlwaysVerifiesSql()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<ParcelLifecycleDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new ParcelLifecycleDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var mirror = new RecordingMirror();
        var wrapper = new RedisMirroringParcelLifecycleStore(
            new EfParcelLifecycleStore(context),
            Coordinator(mirror));
        var record = CompletedRecord("parcel-live", "sql-hash", "sql-response");
        mirror.BeforeStore = async () =>
            Assert.True(await context.IdempotencyRecords.AsNoTracking().AnyAsync(x => x.Key == record.Key));

        wrapper.AddIdempotency(record);
        await wrapper.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(mirror.Stored);
        Assert.True(mirror.Stored.Single().Snapshot.Matches(record));

        mirror.Stored.Clear();
        mirror.Value = IdempotencyCacheSnapshot.FromRecord(
            CompletedRecord("parcel-live", "stale-hash", "stale-response"));
        var authoritative = await wrapper.FindIdempotencyAsync(record.Key, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(authoritative);
        Assert.Equal("sql-hash", authoritative.RequestHash);
        Assert.Single(mirror.Stored);
        Assert.True(mirror.Stored.Single().Snapshot.Matches(authoritative));

        context.IdempotencyRecords.Remove(authoritative);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        mirror.Stored.Clear();
        mirror.Value = IdempotencyCacheSnapshot.FromRecord(record);

        Assert.Null(await wrapper.FindIdempotencyAsync(record.Key, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(mirror.Removed);
    }

    [Fact]
    public async Task FailedParcelSqlCommitNeverWritesMirror()
    {
        var inner = new FailingParcelStore();
        var mirror = new RecordingMirror();
        var wrapper = new RedisMirroringParcelLifecycleStore(inner, Coordinator(mirror));
        wrapper.AddIdempotency(CompletedRecord("failed-commit", "hash", "response"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => wrapper.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Empty(mirror.Stored);
        Assert.Equal(1, inner.SaveAttempts);
    }

    [Fact]
    public async Task ParcelLifecycleDecoratorDelegatesItsCompleteStoreSurface()
    {
        var inner = new FailingParcelStore();
        var wrapper = new RedisMirroringParcelLifecycleStore(inner, Coordinator(new RecordingMirror()));
        var cancellationToken = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<NotSupportedException>(() => wrapper.FindReadingAsync(Guid.NewGuid(), cancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => wrapper.QueryParcelsAsync(null, cancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => wrapper.FindInboxMessageAsync(Guid.NewGuid(), cancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => wrapper.FindPendingAttachmentAsync(Guid.NewGuid(), "object", cancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => wrapper.FindPendingAttachmentByEventAsync(Guid.NewGuid(), cancellationToken));
        await Assert.ThrowsAsync<NotSupportedException>(() => wrapper.GetDuePendingAttachmentsAsync(Now, 10, cancellationToken));
        Assert.Throws<NotSupportedException>(() => wrapper.AddReading(null!));
        Assert.Throws<NotSupportedException>(() => wrapper.AddAttachment(null!));
        Assert.Throws<NotSupportedException>(() => wrapper.AddPendingAttachment(null!));
        Assert.Throws<NotSupportedException>(() => wrapper.RemovePendingAttachment(null!));
        Assert.Throws<NotSupportedException>(() => wrapper.AddInboxMessage(null!));
    }

    [Fact]
    public async Task OperationalResultStoreUsesSqlRecordAndMirrorsAfterCommit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<OperationalResultDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new OperationalResultDbContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var mirror = new RecordingMirror();
        var wrapper = new RedisMirroringOperationalResultStore(
            new EfOperationalResultStore(context),
            context,
            Coordinator(mirror));
        var record = CompletedRecord("operation-live", "operation-hash", "operation-response");

        wrapper.AddIdempotency(record);
        await wrapper.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Single(mirror.Stored);
        mirror.Stored.Clear();
        mirror.Value = null;
        Assert.Null(await wrapper.FindByIdempotencyKeyAsync(record.Key, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(mirror.Stored);
        Assert.True(mirror.Stored.Single().Snapshot.Matches(record));
    }

    [Fact]
    public void InfrastructureRegistrationUsesNullMirrorInTestingAndLiveStoreDecorator()
    {
        var directory = Path.Combine(Path.GetTempPath(), "post-cache-registration", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Redis:Enabled"] = "true",
                    ["Redis:ConnectionString"] = "unreachable.invalid:6379"
                })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddCoreInfrastructure(
                configuration,
                CoreInfrastructureOptions.CreateTesting(directory, CoreServiceProfile.ParcelLifecycle));
            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            Assert.IsType<NullIdempotencyCacheMirror>(
                scope.ServiceProvider.GetRequiredService<IIdempotencyCacheMirror>());
            Assert.IsType<RedisMirroringParcelLifecycleStore>(
                scope.ServiceProvider.GetRequiredService<IParcelLifecycleStore>());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void ConnectedDevelopmentRequiresExplicitRedisTransportChoice()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:Enabled"] = "true",
                ["Redis:ConnectionString"] = "redis:6379"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddCoreInfrastructure(
                configuration,
                new CoreInfrastructureOptions
                {
                    Environment = CoreInfrastructureEnvironment.Development,
                    ServiceProfile = CoreServiceProfile.ParcelLifecycle,
                    DatabaseProvider = CoreDatabaseProvider.SqlServer,
                    ParcelLifecycleConnectionString =
                        "Server=sql;Database=PostParcelLifecycle;User ID=sa;Password=synthetic;Encrypt=True;TrustServerCertificate=True"
                }));

        Assert.Contains("explicit Redis:RequireTls", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRedisCannotDisableTls()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:Enabled"] = "true",
                ["Redis:ConnectionString"] = "redis:6379",
                ["Redis:RequireTls"] = "false"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddCoreInfrastructure(
                configuration,
                new CoreInfrastructureOptions
                {
                    Environment = CoreInfrastructureEnvironment.Production,
                    ServiceProfile = CoreServiceProfile.ParcelLifecycle,
                    DatabaseProvider = CoreDatabaseProvider.SqlServer,
                    ParcelLifecycleConnectionString =
                        "Server=sql;Database=PostParcelLifecycle;User ID=sa;Password=synthetic;Encrypt=True;TrustServerCertificate=False"
                }));

        Assert.Contains("Redis transport must use TLS", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRedisRequiresExplicitAclUsernameAndPassword()
    {
        var connectionStrings = new[]
        {
            string.Join(',', "cache.example.test:6380", "ssl=true", string.Concat("pass", "word=synthetic-test-value")),
            string.Join(',', "cache.example.test:6380", "ssl=true", "user=post-core")
        };
        foreach (var connectionString in connectionStrings)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Redis:Enabled"] = "true",
                    ["Redis:ConnectionString"] = connectionString,
                    ["Redis:RequireTls"] = "true"
                })
                .Build();

            var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
                .AddCoreInfrastructure(
                    configuration,
                    new CoreInfrastructureOptions
                    {
                        Environment = CoreInfrastructureEnvironment.Production,
                        ServiceProfile = CoreServiceProfile.ParcelLifecycle,
                        DatabaseProvider = CoreDatabaseProvider.SqlServer,
                        ParcelLifecycleConnectionString =
                            "Server=sql;Database=PostParcelLifecycle;User ID=sa;Password=synthetic;Encrypt=True;TrustServerCertificate=False"
                    }));

            Assert.Contains("username and password", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-test-value", exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void ProductionCriticalProfilesCannotDisableRedis(string? enabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Redis:Enabled"] = enabled
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(() => new ServiceCollection()
            .AddCoreInfrastructure(
                configuration,
                new CoreInfrastructureOptions
                {
                    Environment = CoreInfrastructureEnvironment.Production,
                    ServiceProfile = CoreServiceProfile.OperationalResult,
                    DatabaseProvider = CoreDatabaseProvider.SqlServer,
                    OperationalResultConnectionString =
                        "Server=sql;Database=PostOperationalResult;User ID=sa;Password=synthetic;Encrypt=True;TrustServerCertificate=False"
                }));

        Assert.Contains("require Redis:Enabled=true", exception.Message, StringComparison.Ordinal);
    }

    private static SqlAuthoritativeIdempotencyCoordinator Coordinator(IIdempotencyCacheMirror mirror) =>
        new(mirror, NullLogger<SqlAuthoritativeIdempotencyCoordinator>.Instance);

    private static IdempotencyRecord CompletedRecord(string key, string hash, string response)
    {
        var record = IdempotencyRecord.Start(new IdempotencyKey(key), hash, Now);
        record.Complete(response, Now.AddSeconds(1));
        return record;
    }

    private sealed class RecordingMirror : IIdempotencyCacheMirror
    {
        public IdempotencyCacheSnapshot? Value { get; set; }

        public Func<Task>? BeforeStore { get; set; }

        public List<(IdempotencyCacheScope Scope, IdempotencyKey Key, IdempotencyCacheSnapshot Snapshot)> Stored { get; } = [];

        public List<(IdempotencyCacheScope Scope, IdempotencyKey Key)> Removed { get; } = [];

        public Task<IdempotencyCacheSnapshot?> TryGetAsync(
            IdempotencyCacheScope scope,
            IdempotencyKey key,
            CancellationToken cancellationToken = default) => Task.FromResult(Value);

        public async Task StoreAsync(
            IdempotencyCacheScope scope,
            IdempotencyKey key,
            IdempotencyCacheSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (BeforeStore is not null)
            {
                await BeforeStore();
            }

            Stored.Add((scope, key, snapshot));
            Value = snapshot;
        }

        public Task RemoveAsync(
            IdempotencyCacheScope scope,
            IdempotencyKey key,
            CancellationToken cancellationToken = default)
        {
            Removed.Add((scope, key));
            Value = null;
            return Task.CompletedTask;
        }

        public Task<int> CleanupExpiredIndexAsync(
            DateTimeOffset now,
            int maximumCount,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class FailingParcelStore : IParcelLifecycleStore
    {
        public int SaveAttempts { get; private set; }

        public Task<IdempotencyRecord?> FindIdempotencyAsync(IdempotencyKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IdempotencyRecord?>(null);

        public void AddIdempotency(IdempotencyRecord record)
        {
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveAttempts++;
            throw new InvalidOperationException("synthetic SQL commit failure");
        }

        public Task<Post.Core.Domain.Parcels.ParcelDossier?> FindParcelAsync(ParcelBarcode barcode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Post.Core.Domain.Parcels.ReadingRecord?> FindReadingAsync(Guid readingRecordId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Post.Core.Domain.Parcels.ParcelDossier>> FindParcelsAsync(IReadOnlyCollection<ParcelBarcode> barcodes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Post.Core.Domain.Parcels.ParcelDossier>> QueryParcelsAsync(IReadOnlyCollection<ExchangeCenterCode>? allowedCenters, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PersistencePage<Post.Core.Domain.Parcels.ParcelDossier>> QueryParcelsPageAsync(IReadOnlyCollection<ExchangeCenterCode>? allowedCenters, string? barcodeContains, int skip, int take, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Post.Core.Domain.Parcels.Bag?> FindBagAsync(BagBarcode barcode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Post.Core.Domain.Parcels.Bag>> FindBagsAsync(IReadOnlyCollection<BagBarcode> barcodes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Post.Core.Domain.Parcels.Dispatch?> FindDispatchAsync(DispatchId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<InboxMessage?> FindInboxMessageAsync(Guid eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Post.Core.Domain.Parcels.PendingAttachment?> FindPendingAttachmentAsync(Guid readingRecordId, string objectKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Post.Core.Domain.Parcels.PendingAttachment?> FindPendingAttachmentByEventAsync(Guid eventId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<Post.Core.Domain.Parcels.PendingAttachment>> GetDuePendingAttachmentsAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void AddParcel(Post.Core.Domain.Parcels.ParcelDossier dossier) => throw new NotSupportedException();
        public void AddReading(Post.Core.Domain.Parcels.ReadingRecord reading) => throw new NotSupportedException();
        public void AddAttachment(Post.Core.Domain.Parcels.SupplementaryAttachment attachment) => throw new NotSupportedException();
        public void AddBag(Post.Core.Domain.Parcels.Bag bag) => throw new NotSupportedException();
        public void AddDispatch(Post.Core.Domain.Parcels.Dispatch dispatch) => throw new NotSupportedException();
        public void AddPendingAttachment(Post.Core.Domain.Parcels.PendingAttachment attachment) => throw new NotSupportedException();
        public void RemovePendingAttachment(Post.Core.Domain.Parcels.PendingAttachment attachment) => throw new NotSupportedException();
        public void AddInboxMessage(InboxMessage message) => throw new NotSupportedException();
        public void AddOutbox(OutboxMessage message) => throw new NotSupportedException();
    }
}
