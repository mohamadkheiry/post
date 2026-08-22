using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;

namespace Post.Core.Infrastructure.Hosting;

public sealed record EdgeHealthMonitorOptions
{
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan StaleThreshold { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record EdgeHealthSweepResult(bool Succeeded, int OfflineTransitions);

public sealed class CoreEdgeHealthMonitorBackgroundService : BackgroundService
{
    private static readonly Action<ILogger, int, Exception?> LogTransitions =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(4, "CoreEdgeHealthOfflineTransitions"),
            "The edge health sweep marked {OfflineTransitions} stale edge nodes offline.");

    private static readonly Action<ILogger, string, Exception?> LogSweepFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(5, "CoreEdgeHealthSweepFailure"),
            "The edge health sweep failed with {ExceptionType} and will be retried on the next interval.");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly EdgeHealthMonitorOptions _options;
    private readonly ILogger<CoreEdgeHealthMonitorBackgroundService> _logger;

    public CoreEdgeHealthMonitorBackgroundService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        EdgeHealthMonitorOptions options,
        ILogger<CoreEdgeHealthMonitorBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<EdgeHealthSweepResult> SweepOnceAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var monitor = scope.ServiceProvider.GetRequiredService<IEdgeHealthMonitor>();
            var transitions = await monitor.MarkStaleEdgesOfflineAsync(
                _options.StaleThreshold,
                cancellationToken).ConfigureAwait(false);
            if (transitions > 0)
            {
                LogTransitions(_logger, transitions, null);
            }

            return new EdgeHealthSweepResult(true, transitions);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CoreApplicationException or
            DbUpdateException or
            TimeoutException or
            InvalidOperationException)
        {
            LogSweepFailure(_logger, exception.GetType().Name, null);
            return new EdgeHealthSweepResult(false, 0);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_options.CheckInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
