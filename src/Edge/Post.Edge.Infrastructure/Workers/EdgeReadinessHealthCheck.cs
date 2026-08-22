using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Persistence;

namespace Post.Edge.Infrastructure.Workers;

public sealed class EdgeReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    IOptions<EdgeStorageOptions> storageOptions,
    IOptions<EdgeLocalAdministratorOptions> localAdministratorOptions) : IHealthCheck
{
    public const string RegistrationName = "edge_sqlite_configuration";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Health checks convert dependency failures to a redacted unhealthy result.")]
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<EdgeDbContext>();
            if (!await database.Database.CanConnectAsync(cancellationToken))
                return HealthCheckResult.Unhealthy("Edge durable storage is unavailable.");
            var configuration = await scope.ServiceProvider.GetRequiredService<IDeviceConfigurationRepository>()
                .GetAsync(cancellationToken);
            if (configuration is null || configuration.Devices.Count == 0)
                return HealthCheckResult.Unhealthy("Edge has no usable persisted device configuration.");
            var availableDiskBytes = scope.ServiceProvider.GetRequiredService<IDiskSpaceProbe>().GetAvailableBytes();
            if (availableDiskBytes < storageOptions.Value.MinimumAvailableDiskBytes)
                return HealthCheckResult.Unhealthy("Edge storage has insufficient available disk space.");
            if (localAdministratorOptions.Value.AuthenticationEnabled)
            {
                var administrators = scope.ServiceProvider.GetRequiredService<ILocalAdministratorRepository>();
                if (!await administrators.ExistsAsync(cancellationToken) ||
                    await administrators.GetAsync(cancellationToken) is null)
                {
                    return HealthCheckResult.Unhealthy(
                        "Edge local-administrator credentials are missing or invalid.");
                }
            }
            var sagaStatus = await scope.ServiceProvider.GetRequiredService<IExternalOperationSagaRepository>()
                .GetStatusAsync(cancellationToken);
            if (sagaStatus.UnresolvedCount > 0)
                return HealthCheckResult.Degraded(
                    $"Edge has {sagaStatus.UnresolvedCount} external operation(s) awaiting manual reconciliation.");
            return HealthCheckResult.Healthy("Edge durable storage, disk, and configuration are ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Edge durable storage or configuration is unavailable.");
        }
    }
}
