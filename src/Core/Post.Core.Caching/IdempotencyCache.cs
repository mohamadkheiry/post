using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.ValueObjects;
using StackExchange.Redis;

namespace Post.Core.Caching;

public interface IIdempotencyCacheMirror
{
    Task<IdempotencyCacheSnapshot?> TryGetAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        CancellationToken cancellationToken = default);

    Task StoreAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        IdempotencyCacheSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        CancellationToken cancellationToken = default);

    Task<int> CleanupExpiredIndexAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default);
}

public sealed class NullIdempotencyCacheMirror : IIdempotencyCacheMirror
{
    public Task<IdempotencyCacheSnapshot?> TryGetAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IdempotencyCacheSnapshot?>(null);

    public Task StoreAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        IdempotencyCacheSnapshot snapshot,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RemoveAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<int> CleanupExpiredIndexAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default) => Task.FromResult(0);
}

public sealed class RedisIdempotencyCacheMirror : IIdempotencyCacheMirror
{
    private const string StoreScript = """
        redis.call('SET', KEYS[1], ARGV[1], 'PX', ARGV[2])
        redis.call('ZADD', KEYS[2], ARGV[3], KEYS[1])
        return 1
        """;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Meter Meter = new("Post.Core.IdempotencyCache", "1.0.0");
    private static readonly Counter<long> CacheOperations = Meter.CreateCounter<long>("post.idempotency_cache.operations");
    private static readonly Action<ILogger, string, string, Exception?> LogCacheUnavailable =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(7101, nameof(LogCacheUnavailable)),
            "Redis idempotency mirror operation {CacheOperation} was unavailable ({ExceptionType}); SQL remains authoritative.");
    private readonly IDatabase _database;
    private readonly RedisIdempotencyCacheOptions _options;
    private readonly ILogger<RedisIdempotencyCacheMirror> _logger;
    private readonly TimeProvider _timeProvider;

    public RedisIdempotencyCacheMirror(
        IConnectionMultiplexer connection,
        RedisIdempotencyCacheOptions options,
        ILogger<RedisIdempotencyCacheMirror> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _database = connection.GetDatabase();
    }

    public async Task<IdempotencyCacheSnapshot?> TryGetAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var value = await _database.StringGetAsync(CacheKey(scope, key)).WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!value.HasValue)
            {
                Record("read", "miss");
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<IdempotencyCacheSnapshot>((string)value!, JsonOptions);
            if (!IsValid(snapshot))
            {
                Record("read", "invalid");
                await RemoveAsync(scope, key, cancellationToken).ConfigureAwait(false);
                return null;
            }

            Record("read", "hit");
            return snapshot;
        }
        catch (JsonException)
        {
            Record("read", "invalid");
            await RemoveIgnoringFailureAsync(scope, key).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsCacheFailure(exception, cancellationToken))
        {
            LogUnavailable("read", exception);
            Record("read", "unavailable");
            return null;
        }
    }

    public async Task StoreAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        IdempotencyCacheSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsValid(snapshot))
        {
            throw new ArgumentException("The idempotency cache snapshot is invalid.", nameof(snapshot));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var cacheKey = CacheKey(scope, key);
            var expiry = _timeProvider.GetUtcNow().Add(_options.EntryTtl).ToUnixTimeMilliseconds();
            var arguments = new RedisValue[]
            {
                JsonSerializer.Serialize(snapshot, JsonOptions),
                checked((long)_options.EntryTtl.TotalMilliseconds),
                expiry
            };
            await _database.ScriptEvaluateAsync(
                    StoreScript,
                    [cacheKey, IndexKey()],
                    arguments)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            Record("write", "stored");
        }
        catch (Exception exception) when (IsCacheFailure(exception, cancellationToken))
        {
            LogUnavailable("write", exception);
            Record("write", "unavailable");
        }
    }

    public async Task RemoveAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var cacheKey = CacheKey(scope, key);
            var transaction = _database.CreateTransaction();
            _ = transaction.KeyDeleteAsync(cacheKey);
            _ = transaction.SortedSetRemoveAsync(IndexKey(), cacheKey.ToString());
            await transaction.ExecuteAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            Record("delete", "removed");
        }
        catch (Exception exception) when (IsCacheFailure(exception, cancellationToken))
        {
            LogUnavailable("delete", exception);
            Record("delete", "unavailable");
        }
    }

    public async Task<int> CleanupExpiredIndexAsync(
        DateTimeOffset now,
        int maximumCount,
        CancellationToken cancellationToken = default)
    {
        if (maximumCount is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var entries = await _database.SortedSetRangeByScoreAsync(
                    IndexKey(),
                    stop: now.ToUnixTimeMilliseconds(),
                    exclude: Exclude.None,
                    order: Order.Ascending,
                    skip: 0,
                    take: maximumCount)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (entries.Length == 0)
            {
                Record("cleanup", "empty");
                return 0;
            }

            var transaction = _database.CreateTransaction();
            foreach (var entry in entries)
            {
                _ = transaction.KeyDeleteAsync((RedisKey)entry.ToString());
            }

            _ = transaction.SortedSetRemoveAsync(IndexKey(), entries);
            var committed = await transaction.ExecuteAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            Record("cleanup", committed ? "removed" : "conflict", entries.Length);
            return committed ? entries.Length : 0;
        }
        catch (Exception exception) when (IsCacheFailure(exception, cancellationToken))
        {
            LogUnavailable("cleanup", exception);
            Record("cleanup", "unavailable");
            return 0;
        }
    }

    private async Task RemoveIgnoringFailureAsync(IdempotencyCacheScope scope, IdempotencyKey key)
    {
        try
        {
            await RemoveAsync(scope, key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCacheFailure(exception, CancellationToken.None))
        {
            LogUnavailable("invalid-entry-delete", exception);
        }
    }

    private RedisKey CacheKey(IdempotencyCacheScope scope, IdempotencyKey key)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.Value)));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{_options.InstancePrefix}:idempotency:{ScopeName(scope)}:{hash}");
    }

    private RedisKey IndexKey() => string.Concat(_options.InstancePrefix, ":idempotency:expiry");

    private static string ScopeName(IdempotencyCacheScope scope) => scope switch
    {
        IdempotencyCacheScope.ParcelLifecycle => "parcel-lifecycle",
        IdempotencyCacheScope.OperationalResult => "operational-result",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown idempotency cache scope.")
    };

    private static bool IsValid(IdempotencyCacheSnapshot? snapshot) =>
        snapshot?.IsStructurallyValid() == true;

    private static bool IsCacheFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is RedisException or TimeoutException or ObjectDisposedException ||
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private void LogUnavailable(string operation, Exception exception) =>
        LogCacheUnavailable(_logger, operation, exception.GetType().Name, null);

    private static void Record(string operation, string outcome, int count = 1) =>
        CacheOperations.Add(
            count,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", outcome));
}

public sealed class SqlAuthoritativeIdempotencyCoordinator
{
    private static readonly Action<ILogger, string, string, Exception?> LogCacheFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(7102, nameof(LogCacheFailure)),
            "Idempotency cache {CacheOperation} failed ({ExceptionType}); SQL remains authoritative.");
    private readonly IIdempotencyCacheMirror _cache;
    private readonly ILogger<SqlAuthoritativeIdempotencyCoordinator> _logger;

    public SqlAuthoritativeIdempotencyCoordinator(
        IIdempotencyCacheMirror cache,
        ILogger<SqlAuthoritativeIdempotencyCoordinator> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IdempotencyRecord?> FindAuthoritativeAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        Func<CancellationToken, Task<IdempotencyRecord?>> loadFromSql,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(loadFromSql);
        var cached = await TryCacheReadAsync(scope, key, cancellationToken).ConfigureAwait(false);
        var authoritative = await loadFromSql(cancellationToken).ConfigureAwait(false);
        if (authoritative is null)
        {
            if (cached is not null)
            {
                await TryCacheRemoveAsync(scope, key).ConfigureAwait(false);
            }

            return null;
        }

        if (cached is null || !cached.Matches(authoritative))
        {
            await TryCacheStoreAsync(scope, key, authoritative).ConfigureAwait(false);
        }

        return authoritative;
    }

    public async Task CommitAndMirrorAsync(
        IdempotencyCacheScope scope,
        IReadOnlyCollection<IdempotencyRecord> records,
        Func<CancellationToken, Task> commitSql,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(commitSql);
        await commitSql(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            await TryCacheStoreAsync(scope, record.Key, record).ConfigureAwait(false);
        }
    }

    private async Task<IdempotencyCacheSnapshot?> TryCacheReadAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.TryGetAsync(scope, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedCacheFailure(exception, cancellationToken))
        {
            LogFailure("read", exception);
            return null;
        }
    }

    private async Task TryCacheStoreAsync(
        IdempotencyCacheScope scope,
        IdempotencyKey key,
        IdempotencyRecord record)
    {
        try
        {
            await _cache.StoreAsync(
                    scope,
                    key,
                    IdempotencyCacheSnapshot.FromRecord(record),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedCacheFailure(exception, CancellationToken.None))
        {
            LogFailure("write", exception);
        }
    }

    private async Task TryCacheRemoveAsync(IdempotencyCacheScope scope, IdempotencyKey key)
    {
        try
        {
            await _cache.RemoveAsync(scope, key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedCacheFailure(exception, CancellationToken.None))
        {
            LogFailure("delete", exception);
        }
    }

    private static bool IsExpectedCacheFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is RedisException or TimeoutException or ObjectDisposedException ||
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private void LogFailure(string operation, Exception exception) =>
        LogCacheFailure(_logger, operation, exception.GetType().Name, null);
}
