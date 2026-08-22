using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Post.Core.Infrastructure;
using Post.Core.Infrastructure.Persistence;

namespace Post.Core.ApiSupport;

public sealed class CoreDatabaseHealthCheck(
    IServiceScopeFactory scopeFactory,
    CoreInfrastructureOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (options.ServiceProfile is CoreServiceProfile.DataIngestion or CoreServiceProfile.ObjectStorage)
        {
            return HealthCheckResult.Healthy("The service is stateless.");
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var canConnect = options.ServiceProfile switch
            {
                CoreServiceProfile.ParcelLifecycle => await CanConnectAsync<ParcelLifecycleDbContext>(scope, cancellationToken),
                CoreServiceProfile.DeviceConfig => await CanConnectAsync<DeviceConfigDbContext>(scope, cancellationToken),
                CoreServiceProfile.EdgeHealth => await CanConnectAsync<EdgeHealthDbContext>(scope, cancellationToken),
                CoreServiceProfile.OperationalResult => await CanConnectAsync<OperationalResultDbContext>(scope, cancellationToken),
                CoreServiceProfile.Identity => await CanConnectAsync<IdentityDbContext>(scope, cancellationToken),
                CoreServiceProfile.All => await CanConnectAllAsync(scope, cancellationToken),
                _ => false
            };
            return canConnect
                ? HealthCheckResult.Healthy("The owned database is reachable.")
                : HealthCheckResult.Unhealthy("The owned database is not reachable.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or DbException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("The owned database health check failed.", exception);
        }
    }

    private static async Task<bool> CanConnectAsync<TContext>(
        AsyncServiceScope scope,
        CancellationToken cancellationToken)
        where TContext : DbContext =>
        await scope.ServiceProvider.GetRequiredService<TContext>().Database
            .CanConnectAsync(cancellationToken).ConfigureAwait(false);

    private static async Task<bool> CanConnectAllAsync(
        AsyncServiceScope scope,
        CancellationToken cancellationToken) =>
        await CanConnectAsync<ParcelLifecycleDbContext>(scope, cancellationToken).ConfigureAwait(false) &&
        await CanConnectAsync<DeviceConfigDbContext>(scope, cancellationToken).ConfigureAwait(false) &&
        await CanConnectAsync<EdgeHealthDbContext>(scope, cancellationToken).ConfigureAwait(false) &&
        await CanConnectAsync<OperationalResultDbContext>(scope, cancellationToken).ConfigureAwait(false) &&
        await CanConnectAsync<IdentityDbContext>(scope, cancellationToken).ConfigureAwait(false);
}
