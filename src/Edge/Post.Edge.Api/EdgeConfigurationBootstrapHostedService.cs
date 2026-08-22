using System.Diagnostics.CodeAnalysis;
using Post.Edge.Application.Ports;
using Post.Edge.Application.Services;

namespace Post.Edge.Api;

internal sealed class EdgeConfigurationBootstrapHostedService(
    IServiceProvider serviceProvider,
    ILogger<EdgeConfigurationBootstrapHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, Exception?> LogCachedConfigurationFallback = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4202, "EdgeConfigurationBootstrapFallback"),
        "Core configuration refresh failed; Edge is continuing with its durable cached snapshot.");

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Startup must use a durable cached snapshot during a transient Core outage.")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<IConfigurationSyncService>().SyncAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            var cached = await scope.ServiceProvider.GetRequiredService<IDeviceConfigurationRepository>()
                .GetAsync(cancellationToken);
            if (cached is null)
                throw new InvalidOperationException(
                    "Edge cannot start because Core configuration is unavailable and no cached snapshot exists.");
            LogCachedConfigurationFallback(logger, null);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
