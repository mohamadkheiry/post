using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Post.Edge.Infrastructure.Integrations.Destination;

#pragma warning disable CA1031 // A health probe converts all adapter failures into a safe, secret-free unhealthy result.

public sealed class DestinationOAuthHealthCheck(DestinationTokenCache tokenCache) : IHealthCheck
{
    public const string RegistrationName = "edge_destination_oauth";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var token = await tokenCache.GetAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(token)
                ? HealthCheckResult.Unhealthy("Destination OAuth verification returned no usable token.")
                : HealthCheckResult.Healthy("Destination OAuth token endpoint is reachable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Destination OAuth token verification failed.");
        }
    }
}
