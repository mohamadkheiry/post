using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Post.ApiSupport;
using Post.Core.ApiSupport;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Infrastructure;
using Post.Core.Identity.Api;

var builder = WebApplication.CreateBuilder(args);
// This host accepts only small control-plane JSON. Bind the shared pre-buffering
// middleware to the stricter authentication limit before service defaults are added.
builder.Configuration["Api:JsonRequestBodyMaximumBytes"] =
    AuthenticationInputLimits.TokenRequestBodyMaximumBytes.ToString(CultureInfo.InvariantCulture);
builder.AddCoreServiceDefaults("Post.Core.Identity.Api", CoreServiceProfile.Identity);
var authenticationRateLimit = ReadAuthenticationRateLimit(builder.Configuration);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, _) =>
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var metadata)
            ? metadata
            : authenticationRateLimit.Window;
        context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return ValueTask.CompletedTask;
    };
    options.AddPolicy("identity-auth", _ => RateLimitPartition.GetFixedWindowLimiter(
        "identity-service",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authenticationRateLimit.PermitLimit,
            Window = authenticationRateLimit.Window,
            QueueLimit = authenticationRateLimit.QueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        }));
});

var app = builder.Build();
app.UsePostApiDefaults();
app.Use(async (context, next) =>
{
    var isTokenEndpoint = HttpMethods.IsPost(context.Request.Method) &&
        (context.Request.Path == "/api/auth/token" ||
         context.Request.Path == "/api/auth/edge-token");
    if (isTokenEndpoint &&
        context.Request.ContentLength > AuthenticationInputLimits.TokenRequestBodyMaximumBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    var requestSizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (isTokenEndpoint && requestSizeFeature is { IsReadOnly: false })
    {
        requestSizeFeature.MaxRequestBodySize = AuthenticationInputLimits.TokenRequestBodyMaximumBytes;
    }

    await next(context).ConfigureAwait(false);
});
app.UseRateLimiter();

app.MapPost("/api/auth/token", AuthenticateAdministratorAsync)
    .AllowAnonymous()
    .RequireRateLimiting("identity-auth")
    .WithMetadata(new RequestSizeLimitAttribute(AuthenticationInputLimits.TokenRequestBodyMaximumBytes))
    .WithName("AuthenticateAdministrator")
    .WithSummary("Authenticates an administrator and returns a signed JWT.");
app.MapPost("/api/auth/edge-token", AuthenticateEdgeAsync)
    .AllowAnonymous()
    .RequireRateLimiting("identity-auth")
    .WithMetadata(new RequestSizeLimitAttribute(AuthenticationInputLimits.TokenRequestBodyMaximumBytes))
    .WithName("AuthenticateEdge")
    .WithSummary("Authenticates an Edge device and returns a short-lived signed JWT.");
app.MapPost("/api/admin/edges", ProvisionEdgeAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("ProvisionEdgeCredential")
    .WithSummary("Provisions an Edge credential and returns its raw secret exactly once.");
app.MapPost("/api/admin/edges/{deviceId:guid}/rotate-secret", RotateEdgeSecretAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("RotateEdgeCredentialSecret")
    .WithSummary("Rotates an Edge secret and returns the replacement exactly once.");
app.MapPost("/api/admin/edges/{deviceId:guid}/disable", DisableEdgeAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("DisableEdgeCredential");

await app.InitializeCoreServiceAsync();
await SeedInitialAdministratorAsync(app);
app.Run();

static async Task<IResult> AuthenticateAdministratorAsync(
    AdminTokenRequest request,
    IAdminAuthService authentication,
    IConfiguration configuration,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    if (string.IsNullOrWhiteSpace(request.UserName) ||
        request.UserName.Length > AuthenticationInputLimits.UserNameMaximumLength ||
        string.IsNullOrWhiteSpace(request.Password) ||
        request.Password.Length > AuthenticationInputLimits.PasswordMaximumLength)
    {
        throw new CoreValidationException("User name or password is invalid.");
    }

    var lifetime = ReadTokenLifetime(
        request.TokenLifetimeSeconds,
        configuration,
        "Auth:AdminTokenMaximumLifetimeSeconds",
        3600);
    try
    {
        var token = await authentication.AuthenticateAsync(
            new AuthenticateAdminCommand(request.UserName, request.Password, lifetime),
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(token);
    }
    catch (CoreAuthorizationException exception)
    {
        return AuthenticationFailure(exception.Message);
    }
}

static async Task<IResult> AuthenticateEdgeAsync(
    EdgeTokenRequest request,
    IEdgeAuthService authentication,
    IConfiguration configuration,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    if (string.IsNullOrWhiteSpace(request.LogicalDeviceCode) ||
        request.LogicalDeviceCode.Length > AuthenticationInputLimits.LogicalDeviceCodeMaximumLength ||
        string.IsNullOrWhiteSpace(request.DeviceSecret) ||
        request.DeviceSecret.Length > AuthenticationInputLimits.DeviceSecretMaximumLength)
    {
        throw new CoreValidationException("Logical device code or device secret is invalid.");
    }

    var lifetime = ReadTokenLifetime(
        request.TokenLifetimeSeconds,
        configuration,
        "Auth:EdgeTokenMaximumLifetimeSeconds",
        900);
    try
    {
        var token = await authentication.AuthenticateAsync(
            new AuthenticateEdgeCommand(request.LogicalDeviceCode, request.DeviceSecret, lifetime),
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(token);
    }
    catch (CoreAuthorizationException exception)
    {
        return AuthenticationFailure(exception.Message);
    }
}

static async Task<IResult> DisableEdgeAsync(
    Guid deviceId,
    ClaimsPrincipal user,
    HttpContext context,
    IEdgeAuthService authentication,
    IIdentityStore store,
    CancellationToken cancellationToken)
{
    var credential = await store.FindEdgeCredentialAsync(
        new Post.Core.Domain.ValueObjects.DeviceId(deviceId),
        cancellationToken).ConfigureAwait(false) ?? throw new CoreNotFoundException("Edge credential does not exist.");
    EnsureCenterAccess(user, credential.ExchangeCenterCode.Value);
    await authentication.DisableAsync(
        deviceId,
        context.Request.RequireIdempotencyKey(),
        user.Actor(),
        context.CorrelationId(),
        cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
}

static async Task<IResult> ProvisionEdgeAsync(
    ProvisionEdgeRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IEdgeAuthService authentication,
    ISecretHasher secretHasher,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    EnsureCenterAccess(user, request.ExchangeCenterCode);
    var idempotencyKey = context.Request.RequireIdempotencyKey();
    var rawSecret = secretHasher.GenerateSecret();
    var result = await authentication.ProvisionAsync(
        new ProvisionEdgeCredentialCommand(
            request.DeviceId,
            request.LogicalDeviceCode,
            request.ExchangeCenterCode,
            rawSecret,
            user.Actor(),
            context.CorrelationId(),
            idempotencyKey),
        cancellationToken).ConfigureAwait(false);
    var response = new ProvisionedEdgeResult(
        result.DeviceId,
        result.DeviceSecret,
        result.IsReplay,
        result.RecoveryRequired);
    return result.IsReplay
        ? Results.Ok(response)
        : Results.Json(response, statusCode: StatusCodes.Status201Created);
}

static async Task<IResult> RotateEdgeSecretAsync(
    Guid deviceId,
    ClaimsPrincipal user,
    HttpContext context,
    IEdgeAuthService authentication,
    IIdentityStore store,
    ISecretHasher secretHasher,
    CancellationToken cancellationToken)
{
    var credential = await store.FindEdgeCredentialAsync(new Post.Core.Domain.ValueObjects.DeviceId(deviceId), cancellationToken)
        .ConfigureAwait(false) ?? throw new CoreNotFoundException("Edge credential does not exist.");
    EnsureCenterAccess(user, credential.ExchangeCenterCode.Value);
    var idempotencyKey = context.Request.RequireIdempotencyKey();
    var rawSecret = secretHasher.GenerateSecret();
    var result = await authentication.RotateSecretAsync(
        new RotateEdgeCredentialCommand(
            deviceId,
            rawSecret,
            idempotencyKey,
            user.Actor(),
            context.CorrelationId()),
        cancellationToken).ConfigureAwait(false);
    var response = new ProvisionedEdgeResult(
        result.DeviceId,
        result.DeviceSecret,
        result.IsReplay,
        result.RecoveryRequired);
    return Results.Ok(response);
}

static void EnsureCenterAccess(ClaimsPrincipal user, string centerCode)
{
    var scope = user.ToAccessScope();
    if (!scope.IsGlobal && !scope.AllowedCenterCodes.Contains(centerCode, StringComparer.Ordinal))
    {
        throw new CoreAuthorizationException("The caller is not allowed to provision an Edge for this center.");
    }
}

static TimeSpan ReadTokenLifetime(
    int? requestedSeconds,
    IConfiguration configuration,
    string maximumConfigurationKey,
    int defaultMaximumSeconds)
{
    var maximumSeconds = configuration.GetValue(maximumConfigurationKey, defaultMaximumSeconds);
    var seconds = requestedSeconds ?? maximumSeconds;
    if (maximumSeconds <= 0 || seconds <= 0 || seconds > maximumSeconds)
    {
        throw new CoreValidationException($"Token lifetime must be between 1 and {maximumSeconds} seconds.");
    }

    return TimeSpan.FromSeconds(seconds);
}

static IdentityAuthenticationRateLimitOptions ReadAuthenticationRateLimit(IConfiguration configuration)
{
    var permitLimit = configuration.GetValue("Identity:AuthenticationRateLimit:PermitLimit", 600);
    var windowSeconds = configuration.GetValue("Identity:AuthenticationRateLimit:WindowSeconds", 60);
    var queueLimit = configuration.GetValue("Identity:AuthenticationRateLimit:QueueLimit", 0);
    if (permitLimit is <= 0 or > 1_000 ||
        windowSeconds is <= 0 or > 3_600 ||
        queueLimit is < 0 or > 100)
    {
        throw new InvalidOperationException(
            "Identity authentication rate-limit settings are outside their safe ranges.");
    }

    return new IdentityAuthenticationRateLimitOptions(
        permitLimit,
        TimeSpan.FromSeconds(windowSeconds),
        queueLimit);
}

static IResult AuthenticationFailure(string detail) => Results.Problem(
    statusCode: StatusCodes.Status401Unauthorized,
    title: "Authentication failed",
    detail: detail,
    type: "https://httpstatuses.io/401",
    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["code"] = "authentication.failed"
    });

static async Task SeedInitialAdministratorAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var identity = scope.ServiceProvider.GetRequiredService<IAdminIdentityAuthority>();
    await identity.EnsureRolesAsync().ConfigureAwait(false);
    if (await identity.HasAnyUserAsync().ConfigureAwait(false))
    {
        return;
    }

    var section = app.Configuration.GetSection("Identity:InitialAdmin");
    if (!section.GetValue("Enabled", false))
    {
        if (app.Environment.IsProduction())
        {
            throw new InvalidOperationException(
                "Identity has no administrator. Configure Identity:InitialAdmin through a protected secret source for first startup.");
        }

        return;
    }

    var userName = section["UserName"];
    var password = section["Password"];
    if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
    {
        throw new InvalidOperationException(
            "Identity:InitialAdmin:UserName and Password must come from a protected configuration source when bootstrap is enabled.");
    }

    var configuredRoles = section.GetSection("Roles").Get<string[]>() ?? [nameof(AdminRole.SystemAdministrator)];
    var roles = configuredRoles.Select(ParseAdminRole).Distinct().ToArray();
    var centers = section.GetSection("AllowedCenterCodes").Get<string[]>() ?? [];
    var provisioning = scope.ServiceProvider.GetRequiredService<IAdminProvisioningService>();
    try
    {
        await provisioning.ProvisionInitialAsync(
            new ProvisionInitialAdminCommand(
                userName,
                password,
                roles,
                centers,
                Guid.NewGuid().ToString("N")),
            CancellationToken.None).ConfigureAwait(false);
    }
    catch (CoreConflictException)
    {
        if (!await identity.HasAnyUserAsync().ConfigureAwait(false))
        {
            throw;
        }

        // Another instance won the one-time bootstrap race.
    }
}

static AdminRole ParseAdminRole(string value) =>
    Enum.TryParse<AdminRole>(value, ignoreCase: false, out var role)
        ? role
        : throw new InvalidOperationException($"Identity initial administrator role '{value}' is invalid.");
