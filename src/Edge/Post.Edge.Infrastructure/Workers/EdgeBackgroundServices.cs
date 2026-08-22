using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Post.Edge.Application.Services;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Persistence;

namespace Post.Edge.Infrastructure.Workers;

internal sealed class EdgeDatabaseInitializer(
    IServiceProvider serviceProvider,
    IOptions<EdgeStorageOptions> storageOptions,
    IHostEnvironment? environment = null,
    IEdgeStorageMaintenance? storageMaintenance = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (storageOptions.Value.EncryptionEnabled)
        {
            var maintenance = storageMaintenance ??
                              serviceProvider.GetRequiredService<IEdgeStorageMaintenance>();
            await maintenance.PrepareAsync(cancellationToken);
        }
        await using var scope = serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
        if (storageOptions.Value.ApplyMigrationsOnStartup)
        {
            await db.Database.MigrateAsync(cancellationToken);
        }
        else
        {
            if (environment is null ||
                (!environment.IsDevelopment() && !environment.IsEnvironment("Testing")))
                throw new InvalidOperationException(
                    "Disabling Edge migrations is permitted only in Development or Testing.");
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.DeviceSessions.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class OutboxWorker(
    IServiceProvider serviceProvider,
    IOptions<EdgeWorkerOptions> options,
    IOutboxWakeSignal wakeSignal,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogCycleFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4101, "EdgeOutboxCycleFailed"),
        "Edge outbox cycle failed and will be retried.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        do
        {
            try
            {
                await using var scope = serviceProvider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OutboxProcessor>()
                    .ProcessBatchAsync(cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
#pragma warning disable CA1031 // A failed provider cycle must not terminate the durable retry worker.
            catch (Exception)
            {
                LogCycleFailure(logger, null);
            }
#pragma warning restore CA1031
            if (!stoppingToken.IsCancellationRequested)
                await wakeSignal.WaitAsync(options.Value.PollInterval, stoppingToken);
        } while (!stoppingToken.IsCancellationRequested);
    }
}

internal sealed class PendingWorkWorker(
    IServiceProvider serviceProvider,
    IOptions<EdgeWorkerOptions> options,
    ILogger<PendingWorkWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogCycleFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4102, "EdgePendingCycleFailed"),
        "Edge pending-work cycle failed and will be retried.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.PollInterval);
        do
        {
            try
            {
                await using var scope = serviceProvider.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IPendingWorkService>();
                await service.ProcessInboundAsync(options.Value.BatchSize, stoppingToken);
                await service.ProcessBagClosuresAsync(options.Value.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
#pragma warning disable CA1031 // A failed provider cycle must not terminate the durable retry worker.
            catch (Exception)
            {
                LogCycleFailure(logger, null);
            }
#pragma warning restore CA1031
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }
}
