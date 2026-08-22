using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Post.Core.Caching;

public sealed class IdempotencyTtlCleanup(
    IIdempotencyCacheMirror cache,
    RedisIdempotencyCacheOptions options,
    TimeProvider timeProvider)
{
    public Task<int> RunOnceAsync(CancellationToken cancellationToken = default) =>
        cache.CleanupExpiredIndexAsync(
            timeProvider.GetUtcNow(),
            options.CleanupBatchSize,
            cancellationToken);
}

public interface IIdempotencyCleanupDelay
{
    Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken);
}

public sealed class TimeProviderIdempotencyCleanupDelay(TimeProvider timeProvider) : IIdempotencyCleanupDelay
{
    public Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken) =>
        Task.Delay(interval, timeProvider, cancellationToken);
}

public sealed class IdempotencyTtlCleanupBackgroundService(
    IdempotencyTtlCleanup cleanup,
    RedisIdempotencyCacheOptions options,
    IIdempotencyCleanupDelay delay,
    ILogger<IdempotencyTtlCleanupBackgroundService> logger) : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogCleanupFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(7103, nameof(LogCleanupFailure)),
            "Redis idempotency index cleanup failed ({ExceptionType}); SQL data was not affected.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ = await cleanup.RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                LogCleanupFailure(logger, exception.GetType().Name, null);
            }

            try
            {
                await delay.WaitAsync(options.CleanupInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}

public sealed class RedisIdempotencyCacheHealthCheck(
    IConnectionMultiplexer connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var latency = await connection.GetDatabase().PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(
                $"Redis idempotency mirror responded in {latency.TotalMilliseconds:F3} ms.");
        }
        catch (Exception exception) when (
            exception is RedisException or TimeoutException or ObjectDisposedException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded(
                $"Redis idempotency mirror is unavailable ({exception.GetType().Name}); SQL remains authoritative.");
        }
    }
}

public static class IdempotencyCacheServiceCollectionExtensions
{
    public static IServiceCollection AddRedisIdempotencyCache(
        this IServiceCollection services,
        RedisIdempotencyCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SqlAuthoritativeIdempotencyCoordinator>();
        if (!options.Enabled)
        {
            services.AddSingleton<IIdempotencyCacheMirror, NullIdempotencyCacheMirror>();
            return services;
        }

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var configuration = ConfigurationOptions.Parse(options.ConnectionString!, true);
            configuration.AbortOnConnectFail = false;
            configuration.ConnectRetry = 1;
            configuration.ConnectTimeout = Math.Min(configuration.ConnectTimeout, 5_000);
            configuration.AsyncTimeout = Math.Min(configuration.AsyncTimeout, 2_000);
            configuration.SyncTimeout = Math.Min(configuration.SyncTimeout, 2_000);
            return ConnectionMultiplexer.Connect(configuration);
        });
        services.AddSingleton<IIdempotencyCacheMirror, RedisIdempotencyCacheMirror>();
        services.AddSingleton<IdempotencyTtlCleanup>();
        services.AddSingleton<IIdempotencyCleanupDelay, TimeProviderIdempotencyCleanupDelay>();
        services.AddHostedService<IdempotencyTtlCleanupBackgroundService>();
        services.AddHealthChecks()
            .AddCheck<RedisIdempotencyCacheHealthCheck>("redis_idempotency_cache", tags: ["cache"]);
        return services;
    }
}
