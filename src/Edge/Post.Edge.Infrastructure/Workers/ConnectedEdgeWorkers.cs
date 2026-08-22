using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Application.Services;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Persistence;

namespace Post.Edge.Infrastructure.Workers;

internal sealed class ConfigurationPollingWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EdgeWorkerOptions> options,
    ILogger<ConfigurationPollingWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4103, "EdgeConfigurationPollFailed"),
        "Periodic Edge configuration synchronization failed; the persisted snapshot remains active.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.ConfigurationSyncInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IConfigurationSyncService>()
                    .SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                LogFailure(logger, null);
            }
        }
    }
}

internal sealed class CoreEdgeHealthWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EdgeWorkerOptions> workerOptions,
    IOptions<CoreApiOptions> coreOptions,
    IClock clock,
    IDiskSpaceProbe diskSpace,
    ILogger<CoreEdgeHealthWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4104, "CoreEdgeHealthPublishFailed"),
        "Core Edge registration or heartbeat failed and will be retried.");
    private static readonly string SoftwareVersion =
        typeof(CoreEdgeHealthWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CoreEdgeHealthWorker).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var registered = false;
        using var timer = new PeriodicTimer(workerOptions.Value.HeartbeatInterval);
        do
        {
            try
            {
                registered = await PublishAsync(registered, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                LogFailure(logger, null);
                registered = false;
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<bool> PublishAsync(bool registered, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var configuration = await scope.ServiceProvider.GetRequiredService<IDeviceConfigurationRepository>()
            .GetAsync(cancellationToken);
        if (configuration is null) return false;

        var settings = coreOptions.Value;
        var client = scope.ServiceProvider.GetRequiredService<ICoreEdgeHealthClient>();
        if (!registered)
        {
            await client.RegisterAsync(new CoreEdgeRegistration(
                settings.EdgeId,
                settings.ExchangeCenterCode,
                SoftwareVersion,
                configuration.Version,
                clock.UtcNow), cancellationToken);
        }

        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        var pending = await db.Outbox.CountAsync(row => row.ProcessedAt == null, cancellationToken)
                      + await db.PendingInbounds.CountAsync(cancellationToken)
                      + await db.PendingBagClosures.CountAsync(row => row.State == 1, cancellationToken);
        var failed = await db.Outbox.CountAsync(
            row => row.ProcessedAt == null && row.Attempts > 0,
            cancellationToken);
        var deadLetters = await db.DeadLetters.CountAsync(cancellationToken);
        await client.SendHeartbeatAsync(new CoreEdgeHeartbeat(
            settings.EdgeId,
            clock.UtcNow,
            SoftwareVersion,
            configuration.Version,
            diskSpace.GetAvailableBytes(),
            pending,
            failed,
            deadLetters,
            configuration.GeneratedAt), cancellationToken);
        return true;
    }
}
