using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.ValueObjects;
using StackExchange.Redis;

namespace Post.Core.Caching.Tests;

public sealed class IdempotencyCacheSnapshotTests
{
    [Fact]
    public void SnapshotRoundTripsRecordSemantics()
    {
        var record = CompletedRecord("request-a", "response-a");

        var snapshot = IdempotencyCacheSnapshot.FromRecord(record);

        Assert.True(snapshot.Matches(record));
        Assert.Equal(IdempotencyCacheSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
    }

    [Fact]
    public void SnapshotRejectsMismatchedOrNullRecord()
    {
        var snapshot = IdempotencyCacheSnapshot.FromRecord(CompletedRecord("request-a", "response-a"));

        Assert.False(snapshot.Matches(CompletedRecord("request-b", "response-a")));
        Assert.Throws<ArgumentNullException>(() => snapshot.Matches(null!));
        Assert.Throws<ArgumentNullException>(() => IdempotencyCacheSnapshot.FromRecord(null!));
    }

    [Fact]
    public void SnapshotTreatsNullOrMalformedPayloadAsInvalid()
    {
        var nullResponse = new IdempotencyCacheSnapshot(
            IdempotencyCacheSnapshot.CurrentSchemaVersion,
            "hash",
            null!,
            Post.Core.Domain.Common.IdempotencyStatus.Completed,
            DateTimeOffset.UtcNow);
        var unknownSchema = nullResponse with { ResponseSnapshot = "{}", SchemaVersion = 99 };

        Assert.False(nullResponse.IsStructurallyValid());
        Assert.False(unknownSchema.IsStructurallyValid());
    }

    private static IdempotencyRecord CompletedRecord(string hash, string response)
    {
        var now = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
        var record = IdempotencyRecord.Start(new IdempotencyKey("cache-test"), hash, now);
        record.Complete(response, now.AddSeconds(1));
        return record;
    }
}

public sealed class RedisIdempotencyCacheOptionsTests
{
    [Fact]
    public void DisabledOptionsDoNotRequireConnectionString()
    {
        var options = new RedisIdempotencyCacheOptions();

        Assert.Same(options, options.Validate());
    }

    [Theory]
    [InlineData(null, "post:core", false)]
    [InlineData("localhost:6379", "contains space", false)]
    [InlineData("localhost:6379", "post:core", true)]
    public void InvalidOrInsecureEnabledOptionsFailClosed(
        string? connection,
        string prefix,
        bool requireTls)
    {
        var options = new RedisIdempotencyCacheOptions
        {
            Enabled = true,
            ConnectionString = connection,
            InstancePrefix = prefix,
            RequireTls = requireTls
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0, 300, 500)]
    [InlineData(31 * 24 * 60, 300, 500)]
    [InlineData(60, 1, 500)]
    [InlineData(60, 300, 0)]
    [InlineData(60, 300, 10001)]
    public void UnsafeTtlCleanupSettingsAreRejected(int ttlMinutes, int cleanupSeconds, int batchSize)
    {
        var options = new RedisIdempotencyCacheOptions
        {
            Enabled = true,
            ConnectionString = "localhost:6379",
            EntryTtl = TimeSpan.FromMinutes(ttlMinutes),
            CleanupInterval = TimeSpan.FromSeconds(cleanupSeconds),
            CleanupBatchSize = batchSize
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Fact]
    public void ValidTlsConfigurationIsAccepted()
    {
        var options = new RedisIdempotencyCacheOptions
        {
            Enabled = true,
            ConnectionString = "cache.example.test:6380,ssl=true,abortConnect=false",
            RequireTls = true
        };

        Assert.Same(options, options.Validate());
    }

    [Fact]
    public void RequiredAclAuthenticationRejectsMissingUsernameOrPassword()
    {
        var connectionStrings = new[]
        {
            string.Join(',', "cache.example.test:6380", "ssl=true", string.Concat("pass", "word=synthetic-test-value")),
            string.Join(',', "cache.example.test:6380", "ssl=true", "user=post-core")
        };
        foreach (var connectionString in connectionStrings)
        {
            var options = new RedisIdempotencyCacheOptions
            {
                Enabled = true,
                ConnectionString = connectionString,
                RequireTls = true,
                RequireAuthentication = true
            };

            var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

            Assert.Contains("username and password", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-test-value", exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RequiredAclAuthenticationAcceptsExplicitUsernameAndPassword()
    {
        var options = new RedisIdempotencyCacheOptions
        {
            Enabled = true,
            ConnectionString = AuthenticatedTlsConnectionString(),
            RequireTls = true,
            RequireAuthentication = true
        };

        Assert.Same(options, options.Validate());
    }

    [Fact]
    public void InvalidConnectionStringDoesNotExposeItsSecretMarker()
    {
        var marker = "sentinel-redis-secret";
        var options = new RedisIdempotencyCacheOptions
        {
            Enabled = true,
            ConnectionString = string.Concat(
                "cache.example.test:6380,",
                "pass",
                "word=",
                marker,
                ",ssl=not-a-boolean")
        };

        var exception = Assert.Throws<InvalidOperationException>(() => options.Validate());

        Assert.DoesNotContain(marker, exception.ToString(), StringComparison.Ordinal);
    }

    private static string AuthenticatedTlsConnectionString() => string.Join(
        ',',
        "cache.example.test:6380",
        "ssl=true",
        "user=post-core",
        string.Concat("pass", "word=synthetic-test-value"),
        "abortConnect=false");
}

public sealed class SqlAuthoritativeIdempotencyCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CacheMissReadsSqlAndPopulatesMirror()
    {
        var cache = new RecordingCache();
        var coordinator = CreateCoordinator(cache);
        var authoritative = CompletedRecord("key-1", "hash-1", "response-1");

        var result = await coordinator.FindAuthoritativeAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            authoritative.Key,
            _ => Task.FromResult<IdempotencyRecord?>(authoritative), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(authoritative, result);
        Assert.Single(cache.Stored);
        Assert.True(cache.Stored.Single().Snapshot.Matches(authoritative));
    }

    [Fact]
    public async Task MatchingCacheHitIsStillVerifiedBySqlWithoutRewrite()
    {
        var authoritative = CompletedRecord("key-2", "hash-2", "response-2");
        var cache = new RecordingCache
        {
            Value = IdempotencyCacheSnapshot.FromRecord(authoritative)
        };
        var coordinator = CreateCoordinator(cache);
        var sqlReads = 0;

        var result = await coordinator.FindAuthoritativeAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            authoritative.Key,
            _ =>
            {
                sqlReads++;
                return Task.FromResult<IdempotencyRecord?>(authoritative);
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(authoritative, result);
        Assert.Equal(1, sqlReads);
        Assert.Empty(cache.Stored);
    }

    [Fact]
    public async Task StaleOrConflictingCacheIsReplacedFromSql()
    {
        var authoritative = CompletedRecord("key-3", "new-hash", "new-response");
        var stale = CompletedRecord("key-3", "old-hash", "old-response");
        var cache = new RecordingCache { Value = IdempotencyCacheSnapshot.FromRecord(stale) };
        var coordinator = CreateCoordinator(cache);

        var result = await coordinator.FindAuthoritativeAsync(
            IdempotencyCacheScope.OperationalResult,
            authoritative.Key,
            _ => Task.FromResult<IdempotencyRecord?>(authoritative), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(authoritative, result);
        Assert.Single(cache.Stored);
        Assert.True(cache.Stored.Single().Snapshot.Matches(authoritative));
    }

    [Fact]
    public async Task StaleCacheEntryIsRemovedWhenSqlHasNoRecord()
    {
        var cached = CompletedRecord("key-4", "hash", "response");
        var cache = new RecordingCache { Value = IdempotencyCacheSnapshot.FromRecord(cached) };
        var coordinator = CreateCoordinator(cache);

        var result = await coordinator.FindAuthoritativeAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            cached.Key,
            _ => Task.FromResult<IdempotencyRecord?>(null), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Single(cache.Removed);
    }

    [Fact]
    public async Task CacheOutageIsNonfatalAndSqlRemainsAuthoritative()
    {
        var authoritative = CompletedRecord("key-5", "hash", "response");
        var cache = new RecordingCache { Failure = new TimeoutException("synthetic outage") };
        var coordinator = CreateCoordinator(cache);

        var result = await coordinator.FindAuthoritativeAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            authoritative.Key,
            _ => Task.FromResult<IdempotencyRecord?>(authoritative), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(authoritative, result);
    }

    [Fact]
    public async Task CommitOccursBeforeCacheWriteAndFailedCommitNeverPopulatesCache()
    {
        var record = CompletedRecord("key-6", "hash", "response");
        var cache = new RecordingCache();
        var coordinator = CreateCoordinator(cache);
        var sequence = new List<string>();
        cache.OnStore = () => sequence.Add("cache");

        await coordinator.CommitAndMirrorAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            [record],
            _ =>
            {
                sequence.Add("sql");
                return Task.CompletedTask;
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["sql", "cache"], sequence);

        cache.Stored.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.CommitAndMirrorAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            [record],
            _ => throw new InvalidOperationException("synthetic sql failure"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(cache.Stored);
    }

    [Fact]
    public async Task CacheWriteFailureAfterCommitDoesNotChangeSqlOutcome()
    {
        var record = CompletedRecord("key-7", "hash", "response");
        var cache = new RecordingCache { FailWritesOnly = true };
        var coordinator = CreateCoordinator(cache);
        var committed = false;

        await coordinator.CommitAndMirrorAsync(
            IdempotencyCacheScope.OperationalResult,
            [record],
            _ =>
            {
                committed = true;
                return Task.CompletedTask;
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgrammingErrorsFromCacheAreNeverMasked(bool duringWrite)
    {
        var record = CompletedRecord("programming-error", "hash", "response");
        var cache = new RecordingCache
        {
            Failure = duringWrite ? null : new InvalidOperationException("synthetic adapter bug"),
            ProgrammingWriteFailure = duringWrite
        };
        var coordinator = CreateCoordinator(cache);

        if (duringWrite)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => coordinator.CommitAndMirrorAsync(
                IdempotencyCacheScope.ParcelLifecycle,
                [record],
                _ => Task.CompletedTask, cancellationToken: TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.FindAuthoritativeAsync(
                IdempotencyCacheScope.ParcelLifecycle,
                record.Key,
                _ => Task.FromResult<IdempotencyRecord?>(record), cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotSwallowedBeforeSqlRead()
    {
        var cache = new RecordingCache();
        var coordinator = CreateCoordinator(cache);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.FindAuthoritativeAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            new IdempotencyKey("cancelled"),
            _ => Task.FromResult<IdempotencyRecord?>(null),
            source.Token));
    }

    private static SqlAuthoritativeIdempotencyCoordinator CreateCoordinator(IIdempotencyCacheMirror cache) =>
        new(cache, NullLogger<SqlAuthoritativeIdempotencyCoordinator>.Instance);

    private static IdempotencyRecord CompletedRecord(string key, string hash, string response)
    {
        var record = IdempotencyRecord.Start(new IdempotencyKey(key), hash, Now);
        record.Complete(response, Now.AddSeconds(1));
        return record;
    }

    private sealed class RecordingCache : IIdempotencyCacheMirror
    {
        public IdempotencyCacheSnapshot? Value { get; init; }
        public Exception? Failure { get; init; }
        public bool FailWritesOnly { get; init; }
        public bool ProgrammingWriteFailure { get; init; }
        public Action? OnStore { get; set; }
        public List<(IdempotencyCacheScope Scope, IdempotencyKey Key, IdempotencyCacheSnapshot Snapshot)> Stored { get; } = [];
        public List<(IdempotencyCacheScope Scope, IdempotencyKey Key)> Removed { get; } = [];

        public Task<IdempotencyCacheSnapshot?> TryGetAsync(
            IdempotencyCacheScope scope,
            IdempotencyKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return Task.FromResult(Value);
        }

        public Task StoreAsync(
            IdempotencyCacheScope scope,
            IdempotencyKey key,
            IdempotencyCacheSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            if (ProgrammingWriteFailure) throw new ArgumentException("synthetic adapter bug");
            if (Failure is not null || FailWritesOnly)
                throw Failure ?? new TimeoutException("synthetic cache write outage");
            OnStore?.Invoke();
            Stored.Add((scope, key, snapshot));
            return Task.CompletedTask;
        }

        public Task RemoveAsync(
            IdempotencyCacheScope scope,
            IdempotencyKey key,
            CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            Removed.Add((scope, key));
            return Task.CompletedTask;
        }

        public Task<int> CleanupExpiredIndexAsync(
            DateTimeOffset now,
            int maximumCount,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}

public sealed class NullAndHostingCacheTests
{
    [Fact]
    public async Task NullCacheIsACompleteNoop()
    {
        var cache = new NullIdempotencyCacheMirror();
        var key = new IdempotencyKey("noop");
        var snapshot = IdempotencyCacheSnapshot.FromRecord(CompletedRecord(key));

        Assert.Null(await cache.TryGetAsync(IdempotencyCacheScope.ParcelLifecycle, key, cancellationToken: TestContext.Current.CancellationToken));
        await cache.StoreAsync(IdempotencyCacheScope.ParcelLifecycle, key, snapshot, cancellationToken: TestContext.Current.CancellationToken);
        await cache.RemoveAsync(IdempotencyCacheScope.ParcelLifecycle, key, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, await cache.CleanupExpiredIndexAsync(DateTimeOffset.UtcNow, 1, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CleanupDelegatesCurrentTimeAndConfiguredBatch()
    {
        var now = new DateTimeOffset(2026, 8, 16, 11, 0, 0, TimeSpan.Zero);
        var cache = new CleanupRecordingCache();
        var cleanup = new IdempotencyTtlCleanup(
            cache,
            new RedisIdempotencyCacheOptions { CleanupBatchSize = 42 },
            new FixedTimeProvider(now));

        var count = await cleanup.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(7, count);
        Assert.Equal(now, cache.Now);
        Assert.Equal(42, cache.MaximumCount);
    }

    [Fact]
    public void DependencyInjectionUsesNullCacheWhenDisabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddRedisIdempotencyCache(new RedisIdempotencyCacheOptions());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<NullIdempotencyCacheMirror>(provider.GetRequiredService<IIdempotencyCacheMirror>());
        Assert.NotNull(provider.GetRequiredService<SqlAuthoritativeIdempotencyCoordinator>());
    }

    [Fact]
    public void DependencyInjectionRejectsEnabledInvalidOptions()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddRedisIdempotencyCache(
            new RedisIdempotencyCacheOptions { Enabled = true }));
    }

    [Fact]
    public async Task CleanupFailureAlwaysWaitsBeforeRetry()
    {
        var cache = new ThrowingCleanupCache();
        var delay = new BlockingRecordingDelay();
        var options = new RedisIdempotencyCacheOptions
        {
            CleanupBatchSize = 10,
            CleanupInterval = TimeSpan.FromMinutes(1)
        };
        var cleanup = new IdempotencyTtlCleanup(cache, options, TimeProvider.System);
        var worker = new IdempotencyTtlCleanupBackgroundService(
            cleanup,
            options,
            delay,
            NullLogger<IdempotencyTtlCleanupBackgroundService>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, cache.Attempts);
        Assert.Equal(options.CleanupInterval, delay.Interval);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TimeProviderDelayHonorsCancellation()
    {
        var delay = new TimeProviderIdempotencyCleanupDelay(TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            delay.WaitAsync(TimeSpan.FromMinutes(1), cancellation.Token));
    }

    private static IdempotencyRecord CompletedRecord(IdempotencyKey key)
    {
        var now = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
        var record = IdempotencyRecord.Start(key, "hash", now);
        record.Complete("response", now);
        return record;
    }

    private sealed class CleanupRecordingCache : IIdempotencyCacheMirror
    {
        public DateTimeOffset Now { get; private set; }
        public int MaximumCount { get; private set; }

        public Task<IdempotencyCacheSnapshot?> TryGetAsync(IdempotencyCacheScope scope, IdempotencyKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IdempotencyCacheSnapshot?>(null);

        public Task StoreAsync(IdempotencyCacheScope scope, IdempotencyKey key, IdempotencyCacheSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(IdempotencyCacheScope scope, IdempotencyKey key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CleanupExpiredIndexAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken = default)
        {
            Now = now;
            MaximumCount = maximumCount;
            return Task.FromResult(7);
        }
    }

    private sealed class ThrowingCleanupCache : IIdempotencyCacheMirror
    {
        public int Attempts { get; private set; }

        public Task<IdempotencyCacheSnapshot?> TryGetAsync(IdempotencyCacheScope scope, IdempotencyKey key, CancellationToken cancellationToken = default) =>
            Task.FromResult<IdempotencyCacheSnapshot?>(null);

        public Task StoreAsync(IdempotencyCacheScope scope, IdempotencyKey key, IdempotencyCacheSnapshot snapshot, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(IdempotencyCacheScope scope, IdempotencyKey key, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<int> CleanupExpiredIndexAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new InvalidOperationException("synthetic cleanup outage");
        }
    }

    private sealed class BlockingRecordingDelay : IIdempotencyCleanupDelay
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Interval { get; private set; }

        public async Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            Interval = interval;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class RedisIdempotencyCacheMirrorTests
{
    [Fact]
    public async Task MirrorCoversHitMissInvalidStoreRemoveCleanupAndBothScopes()
    {
        var state = new RedisProxyState();
        var mirror = CreateMirror(state);
        var parcelKey = new IdempotencyKey("parcel-key");
        var operationalKey = new IdempotencyKey("operation-key");

        Assert.Null(await mirror.TryGetAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            parcelKey,
            TestContext.Current.CancellationToken));

        var snapshot = Snapshot(parcelKey);
        await mirror.StoreAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            parcelKey,
            snapshot,
            TestContext.Current.CancellationToken);
        Assert.True(state.StoredValue.HasValue);
        state.StringValue = state.StoredValue;
        var hit = await mirror.TryGetAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            parcelKey,
            TestContext.Current.CancellationToken);
        Assert.NotNull(hit);
        Assert.Equal(snapshot.RequestHash, hit.RequestHash);

        await mirror.StoreAsync(
            IdempotencyCacheScope.OperationalResult,
            operationalKey,
            Snapshot(operationalKey),
            TestContext.Current.CancellationToken);
        await mirror.RemoveAsync(
            IdempotencyCacheScope.OperationalResult,
            operationalKey,
            TestContext.Current.CancellationToken);
        Assert.True(state.TransactionExecutions > 0);

        state.StringValue = "{}";
        Assert.Null(await mirror.TryGetAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            parcelKey,
            TestContext.Current.CancellationToken));
        state.StringValue = "{malformed";
        Assert.Null(await mirror.TryGetAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            parcelKey,
            TestContext.Current.CancellationToken));

        state.ExpiredEntries = [];
        Assert.Equal(0, await mirror.CleanupExpiredIndexAsync(
            DateTimeOffset.UtcNow,
            10,
            TestContext.Current.CancellationToken));
        state.ExpiredEntries = ["post:test:idempotency:parcel-lifecycle:expired"];
        Assert.Equal(1, await mirror.CleanupExpiredIndexAsync(
            DateTimeOffset.UtcNow,
            10,
            TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mirror.StoreAsync(
            (IdempotencyCacheScope)999,
            parcelKey,
            snapshot,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mirror.CleanupExpiredIndexAsync(
            DateTimeOffset.UtcNow,
            0,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MirrorAndHealthTreatRedisOutageAsNonfatalButPreserveCallerCancellation()
    {
        var state = new RedisProxyState { Failure = new TimeoutException("synthetic redis outage") };
        var connection = RedisProxies.Connection(state);
        var mirror = CreateMirror(state, connection);
        var key = new IdempotencyKey("outage-key");

        Assert.Null(await mirror.TryGetAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            key,
            TestContext.Current.CancellationToken));
        await mirror.StoreAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            key,
            Snapshot(key),
            TestContext.Current.CancellationToken);
        await mirror.RemoveAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            key,
            TestContext.Current.CancellationToken);
        Assert.Equal(0, await mirror.CleanupExpiredIndexAsync(
            DateTimeOffset.UtcNow,
            1,
            TestContext.Current.CancellationToken));

        var health = new RedisIdempotencyCacheHealthCheck(connection);
        var degraded = await health.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Degraded, degraded.Status);

        state.Failure = null;
        var healthy = await health.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Healthy, healthy.Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mirror.TryGetAsync(
            IdempotencyCacheScope.ParcelLifecycle,
            key,
            cancellation.Token));
    }

    private static RedisIdempotencyCacheMirror CreateMirror(
        RedisProxyState state,
        IConnectionMultiplexer? connection = null) =>
        new(
            connection ?? RedisProxies.Connection(state),
            new RedisIdempotencyCacheOptions
            {
                Enabled = true,
                ConnectionString = "localhost:6379",
                InstancePrefix = "post:test"
            },
            NullLogger<RedisIdempotencyCacheMirror>.Instance,
            TimeProvider.System);

    private static IdempotencyCacheSnapshot Snapshot(IdempotencyKey key)
    {
        var now = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var record = IdempotencyRecord.Start(key, "request-hash", now);
        record.Complete("{\"ok\":true}", now.AddSeconds(1));
        return IdempotencyCacheSnapshot.FromRecord(record);
    }

    private sealed class RedisProxyState
    {
        public RedisValue StringValue { get; set; } = RedisValue.Null;
        public RedisValue StoredValue { get; set; } = RedisValue.Null;
        public RedisValue[] ExpiredEntries { get; set; } = [];
        public Exception? Failure { get; set; }
        public int TransactionExecutions { get; set; }
    }

    private static class RedisProxies
    {
        public static IConnectionMultiplexer Connection(RedisProxyState state)
        {
            var transaction = Proxy<ITransaction>((method, _) =>
            {
                ThrowIfConfigured(state);
                return method.Name switch
                {
                    nameof(ITransaction.ExecuteAsync) => Task.FromResult(++state.TransactionExecutions > 0),
                    nameof(ITransaction.KeyDeleteAsync) => Task.FromResult(true),
                    nameof(ITransaction.SortedSetRemoveAsync) when method.ReturnType == typeof(Task<long>) => Task.FromResult(1L),
                    nameof(ITransaction.SortedSetRemoveAsync) => Task.FromResult(true),
                    _ => Default(method.ReturnType)
                };
            });
            var database = Proxy<IDatabase>((method, arguments) =>
            {
                ThrowIfConfigured(state);
                return method.Name switch
                {
                    nameof(IDatabase.StringGetAsync) => Task.FromResult(state.StringValue),
                    nameof(IDatabase.ScriptEvaluateAsync) => CaptureStore(state, arguments),
                    nameof(IDatabase.CreateTransaction) => transaction,
                    nameof(IDatabase.SortedSetRangeByScoreAsync) => Task.FromResult(state.ExpiredEntries),
                    nameof(IDatabase.PingAsync) => Task.FromResult(TimeSpan.FromMilliseconds(1.25)),
                    _ => Default(method.ReturnType)
                };
            });
            return Proxy<IConnectionMultiplexer>((method, _) =>
                method.Name == nameof(IConnectionMultiplexer.GetDatabase)
                    ? database
                    : Default(method.ReturnType));
        }

        private static Task<RedisResult> CaptureStore(RedisProxyState state, object?[]? arguments)
        {
            var values = Assert.IsType<RedisValue[]>(arguments?[2]);
            state.StoredValue = values[0];
            return Task.FromResult<RedisResult>(null!);
        }

        private static void ThrowIfConfigured(RedisProxyState state)
        {
            if (state.Failure is not null)
            {
                throw state.Failure;
            }
        }

        private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handler)
            where T : class
        {
            var value = DispatchProxy.Create<T, RedisDispatchProxy<T>>();
            ((RedisDispatchProxy<T>)(object)value).Handler = handler;
            return value;
        }

        private static object? Default(Type type)
        {
            if (type == typeof(void))
            {
                return null;
            }

            if (type == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var value = type.GetGenericArguments()[0].IsValueType
                    ? Activator.CreateInstance(type.GetGenericArguments()[0])
                    : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(type.GetGenericArguments()[0])
                    .Invoke(null, [value]);
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }

    private class RedisDispatchProxy<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod ?? throw new InvalidOperationException("Proxy method metadata is missing."), args);
    }

    // DispatchProxy requires an inheritable proxy type; this concrete subtype also
    // prevents the analyzer from incorrectly proposing that the proxy base be sealed.
    private sealed class RedisDispatchProxySubtype<T> : RedisDispatchProxy<T>
        where T : class;
}
