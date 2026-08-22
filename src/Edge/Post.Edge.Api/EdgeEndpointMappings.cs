using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc;
using Post.BuildingBlocks.Correlation;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Application.Services;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Persistence;

namespace Post.Edge.Api;

public sealed record ReplaceDeviceIpBindingsRequest(IReadOnlyCollection<string> IpAddresses);
public sealed record LocalAdministratorLoginRequest(string UserName, string Password);
public sealed record LocalAdministratorPasswordRotationRequest(
    string UserName,
    string CurrentPassword,
    string NewPassword);

internal static class EdgeEndpointMappings
{
    internal static IEndpointRouteBuilder MapEdgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var administratorOptions = endpoints.ServiceProvider
            .GetRequiredService<IOptions<EdgeLocalAdministratorOptions>>().Value;

        endpoints.MapPost("/admin/auth/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting("edge-admin-login")
            .WithMetadata(new RequestSizeLimitAttribute(administratorOptions.MaximumRequestBodyBytes));
        var logout = endpoints.MapPost("/admin/auth/logout", LogoutAsync)
            .WithMetadata(new RequestSizeLimitAttribute(administratorOptions.MaximumRequestBodyBytes));
        var rotatePassword = endpoints.MapPost("/admin/auth/password", RotatePasswordAsync)
            .WithMetadata(new RequestSizeLimitAttribute(administratorOptions.MaximumRequestBodyBytes));

        var replaceBindings = endpoints.MapPut(
            "/admin/devices/{deviceId}/ip",
            ReplaceBindingsAsync)
            .WithMetadata(new RequestSizeLimitAttribute(administratorOptions.MaximumRequestBodyBytes));
        var backup = endpoints.MapPost("/admin/storage/backup", CreateBackupAsync)
            .WithMetadata(new RequestSizeLimitAttribute(administratorOptions.MaximumRequestBodyBytes));
        var status = endpoints.MapGet("/api/edge/status", GetStatusAsync);
        if (administratorOptions.AuthenticationEnabled)
        {
            logout.RequireAuthorization(LocalAdministratorAuthentication.Policy);
            rotatePassword.RequireAuthorization(LocalAdministratorAuthentication.Policy);
            replaceBindings.RequireAuthorization(LocalAdministratorAuthentication.Policy);
            backup.RequireAuthorization(LocalAdministratorAuthentication.Policy);
            status.RequireAuthorization(LocalAdministratorAuthentication.Policy);
        }
        else
        {
            logout.AllowAnonymous();
            rotatePassword.AllowAnonymous();
            replaceBindings.AllowAnonymous();
            backup.AllowAnonymous();
            status.AllowAnonymous();
        }

        return endpoints;
    }

    private static async Task<IResult> ReplaceBindingsAsync(
        string deviceId,
        ReplaceDeviceIpBindingsRequest request,
        IDeviceNetworkBindingAdminService service,
        ICorrelationContext correlation,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return Results.BadRequest(new { error = "Device id is required." });
        if (request is null || request.IpAddresses is null)
            return Results.BadRequest(new { error = "ipAddresses is required." });

        var actor = httpContext.User.FindFirst("sub")?.Value
                    ?? httpContext.User.Identity?.Name
                    ?? "development-local-admin";
        ReplaceDeviceNetworkBindingsResult result = await service.ReplaceAsync(
            new ReplaceDeviceNetworkBindingsCommand(
                deviceId,
                request.IpAddresses,
                actor,
                correlation.GetOrCreate()),
            cancellationToken);
        return Results.Ok(new
        {
            deviceId,
            ipAddresses = result.IpAddresses,
            count = result.IpAddresses.Count
        });
    }

    private static async Task<IResult> GetStatusAsync(
        IDeviceConfigurationRepository configurations,
        IExternalOperationSagaRepository externalOperations,
        IConfiguration configuration,
        IDiskSpaceProbe diskSpace,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var snapshot = await configurations.GetAsync(cancellationToken);
        var externalStatus = await externalOperations.GetStatusAsync(cancellationToken);
        return Results.Ok(new
        {
            service = "Post.Edge.Api",
            status = snapshot is null ? "configuration-unavailable" : "ready",
            utc = timeProvider.GetUtcNow(),
            integrationProfile = configuration["Edge:Integrations:Profile"]
                                 ?? configuration["Edge:Integrations:Mode"]
                                 ?? "LocalConnected",
            configurationVersion = snapshot?.Version,
            centerCode = snapshot?.ExchangeCenterCode,
            activeDeviceCount = snapshot?.Devices.Count(device => device.Active) ?? 0,
            availableDiskBytes = TryGetAvailableDiskBytes(diskSpace),
            unresolvedExternalOperationCount = externalStatus.UnresolvedCount,
            oldestUnresolvedExternalOperationAt = externalStatus.OldestCreatedAt
        });
    }

    private static async Task<IResult> CreateBackupAsync(
        IEdgeStorageMaintenance maintenance,
        IOptions<EdgeStorageOptions> options,
        CancellationToken cancellationToken)
    {
        if (!options.Value.EncryptionEnabled)
            return Results.Conflict(new { error = "Encrypted Edge backups are not enabled." });
        var path = await maintenance.CreateEncryptedBackupAsync(cancellationToken);
        return Results.Ok(new
        {
            backupFile = Path.GetFileName(path),
            encrypted = true
        });
    }

    private static async Task<IResult> LoginAsync(
        LocalAdministratorLoginRequest request,
        ILocalAdministratorService administrators,
        IOptions<EdgeLocalAdministratorOptions> options,
        CancellationToken cancellationToken)
    {
        if (!options.Value.AuthenticationEnabled) return Results.NotFound();
        if (request is null) return Results.Unauthorized();
        var result = await administrators.LoginAsync(
            new LocalAdministratorLoginCommand(request.UserName, request.Password),
            options.Value.SessionLifetime,
            cancellationToken);
        return result is null
            ? Results.Unauthorized()
            : Results.Ok(new
            {
                accessToken = result.AccessToken,
                tokenType = "Bearer",
                expiresAt = result.ExpiresAt
            });
    }

    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        ILocalAdministratorService administrators,
        CancellationToken cancellationToken)
    {
        var token = LocalAdministratorAuthentication.ReadBearerToken(httpContext.Request);
        if (token is not null) await administrators.LogoutAsync(token, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> RotatePasswordAsync(
        LocalAdministratorPasswordRotationRequest request,
        ILocalAdministratorService administrators,
        CancellationToken cancellationToken)
    {
        if (request is null) return Results.Unauthorized();
        var rotated = await administrators.RotatePasswordAsync(
            new RotateLocalAdministratorPasswordCommand(
                request.UserName,
                request.CurrentPassword,
                request.NewPassword),
            cancellationToken);
        return rotated ? Results.NoContent() : Results.Unauthorized();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The status boundary reports unavailable telemetry without exposing drive details.")]
    private static long? TryGetAvailableDiskBytes(IDiskSpaceProbe diskSpace)
    {
        try
        {
            var available = diskSpace.GetAvailableBytes();
            return available >= 0 ? available : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

}
