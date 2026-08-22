using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;

namespace Post.Core.Infrastructure.Hosting;

public sealed class CoreOutboxBackgroundService : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> LogProcessingFailure = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(1, "CoreOutboxFailure"),
        "Core outbox processing failed with {ExceptionType} and will be retried.");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CoreInfrastructureOptions _options;
    private readonly ILogger<CoreOutboxBackgroundService> _logger;

    public CoreOutboxBackgroundService(
        IServiceScopeFactory scopeFactory,
        CoreInfrastructureOptions options,
        ILogger<CoreOutboxBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var published = await PublishOnceAsync(stoppingToken).ConfigureAwait(false);
            if (published == 0)
            {
                await Task.Delay(_options.OutboxPollingInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> PublishOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IOutboxPublisher>();
            return await publisher.PublishBatchAsync(
                _options.OutboxBatchSize,
                _options.OutboxMaximumAttempts,
                cancellationToken).ConfigureAwait(false);
        }
        catch (CoreApplicationException exception)
        {
            LogProcessingFailure(_logger, exception.GetType().Name, null);
            return 0;
        }
        catch (DbUpdateException exception)
        {
            LogProcessingFailure(_logger, exception.GetType().Name, null);
            return 0;
        }
        catch (TimeoutException exception)
        {
            LogProcessingFailure(_logger, exception.GetType().Name, null);
            return 0;
        }
    }
}
