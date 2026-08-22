using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Post.ApiSupport;
using Post.Core.Application;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Infrastructure;

namespace Post.Core.ApiSupport;

public sealed record AuthenticatedEdgeIdentity(Guid EdgeId, string ExchangeCenterCode);

public static class CoreApiHostingExtensions
{
    public static WebApplicationBuilder AddCoreServiceDefaults(
        this WebApplicationBuilder builder,
        string serviceName,
        CoreServiceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        builder.AddPostApiDefaults(serviceName);
        builder.Services.AddCoreApplication(ToApplicationProfile(profile));
        var infrastructure = CreateInfrastructureOptions(builder, profile);
        builder.Services.AddCoreInfrastructure(builder.Configuration, infrastructure);
        builder.Services.AddHealthChecks().AddCheck<CoreDatabaseHealthCheck>("database", tags: ["ready"]);
        return builder;
    }

    public static async Task InitializeCoreServiceAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        await using var scope = app.Services.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetService<ICoreDatabaseInitializer>();
        if (initializer is null)
        {
            return;
        }

        var applyMigrations = app.Configuration.GetValue(
            "Core:Database:ApplyMigrations",
            app.Environment.IsProduction());
        await initializer.InitializeAsync(applyMigrations, cancellationToken).ConfigureAwait(false);
    }

    public static AccessScope ToAccessScope(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.IsInRole("SystemAdministrator") || principal.IsInRole("GlobalAdministrator"))
        {
            return AccessScope.Global;
        }

        var centers = principal.Claims
            .Where(claim => claim.Type is "allowed_center" or "allowed_centers")
            .SelectMany(claim => ParseCenters(claim.Value))
            .Where(center => !string.IsNullOrWhiteSpace(center))
            .Select(center => center.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new AccessScope(false, centers);
    }

    public static string Actor(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue("sub") ??
               principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
               "anonymous";
    }

    public static AuthenticatedEdgeIdentity AuthenticatedEdge(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!Guid.TryParse(principal.FindFirstValue("device_id"), out var edgeId) || edgeId == Guid.Empty)
        {
            throw new CoreAuthorizationException("The authenticated Edge identifier is missing or invalid.");
        }

        var centers = principal.Claims
            .Where(claim => claim.Type is "allowed_center" or "allowed_centers")
            .SelectMany(claim => ParseCenters(claim.Value))
            .Where(center => !string.IsNullOrWhiteSpace(center))
            .Select(center => center.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (centers.Length != 1)
        {
            throw new CoreAuthorizationException(
                "An Edge token must identify exactly one allowed exchange center.");
        }

        return new AuthenticatedEdgeIdentity(edgeId, centers.Single());
    }

    public static string CorrelationId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Response.Headers[Post.BuildingBlocks.Correlation.CorrelationId.HeaderName].FirstOrDefault()
               ?? context.TraceIdentifier;
    }

    public static string RequireIdempotencyKey(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.Headers["Idempotency-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        {
            throw new ArgumentException("A non-empty Idempotency-Key header of at most 200 characters is required.");
        }

        return key.Trim();
    }

    private static CoreInfrastructureOptions CreateInfrastructureOptions(
        WebApplicationBuilder builder,
        CoreServiceProfile profile)
    {
        var outboxEnabled = profile is not CoreServiceProfile.DataIngestion and not CoreServiceProfile.ObjectStorage &&
                            builder.Configuration.GetValue("Core:Outbox:Enabled", true);
        var requestedProvider = builder.Configuration["Core:Database:Provider"]?.Trim();
        if (builder.Environment.IsEnvironment("Testing") ||
            (builder.Environment.IsDevelopment() &&
             (string.IsNullOrEmpty(requestedProvider) ||
              string.Equals(requestedProvider, "Sqlite", StringComparison.OrdinalIgnoreCase))))
        {
            var directory = builder.Configuration["Core:Database:Directory"] ??
                            Path.Combine(builder.Environment.ContentRootPath, "data", "core");
            return CoreInfrastructureOptions.CreateTesting(directory, profile) with
            {
                EnableOutboxProcessing = outboxEnabled,
                OutboxPollingInterval = TimeSpan.FromSeconds(
                    builder.Configuration.GetValue("Core:Outbox:PollingSeconds", 2)),
                OutboxBatchSize = builder.Configuration.GetValue("Core:Outbox:BatchSize", 100),
                OutboxMaximumAttempts = builder.Configuration.GetValue("Core:Outbox:MaximumAttempts", 10)
            };
        }

        if (builder.Environment.IsDevelopment() &&
            !string.Equals(requestedProvider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Development Core database provider must be Sqlite or SqlServer when explicitly configured.");
        }

        if (!string.IsNullOrEmpty(requestedProvider) &&
            !string.Equals(requestedProvider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Production Core services require Core:Database:Provider=SqlServer; SQLite is limited to Development or Testing.");
        }

        return new CoreInfrastructureOptions
        {
            Environment = builder.Environment.IsDevelopment()
                ? CoreInfrastructureEnvironment.Development
                : CoreInfrastructureEnvironment.Production,
            ServiceProfile = profile,
            DatabaseProvider = CoreDatabaseProvider.SqlServer,
            EnableOutboxProcessing = outboxEnabled,
            OutboxPollingInterval = TimeSpan.FromSeconds(
                builder.Configuration.GetValue("Core:Outbox:PollingSeconds", 2)),
            OutboxBatchSize = builder.Configuration.GetValue("Core:Outbox:BatchSize", 100),
            OutboxMaximumAttempts = builder.Configuration.GetValue("Core:Outbox:MaximumAttempts", 10)
        };
    }

    private static CoreApplicationProfile ToApplicationProfile(CoreServiceProfile profile) => profile switch
    {
        CoreServiceProfile.All => CoreApplicationProfile.All,
        CoreServiceProfile.ParcelLifecycle => CoreApplicationProfile.ParcelLifecycle,
        CoreServiceProfile.DeviceConfig => CoreApplicationProfile.DeviceConfig,
        CoreServiceProfile.DataIngestion => CoreApplicationProfile.DataIngestion,
        CoreServiceProfile.ObjectStorage => CoreApplicationProfile.ObjectStorage,
        CoreServiceProfile.EdgeHealth => CoreApplicationProfile.EdgeHealth,
        CoreServiceProfile.OperationalResult => CoreApplicationProfile.OperationalResult,
        CoreServiceProfile.Identity => CoreApplicationProfile.Identity,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported Core service profile.")
    };

    private static string[] ParseCenters(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        if (value.TrimStart().StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(value) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
