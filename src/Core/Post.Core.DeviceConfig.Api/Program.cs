using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Post.ApiSupport;
using Post.Core.ApiSupport;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.DeviceConfig.Api;
using Post.Core.Domain.Common;
using Post.Core.Domain.Devices;
using Post.Core.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddCoreServiceDefaults("Post.Core.DeviceConfig.Api", CoreServiceProfile.DeviceConfig);
builder.Services.Configure<JsonOptions>(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<DeviceActivationStatus>()));

var app = builder.Build();
app.UsePostApiDefaults();

app.MapPost("/api/admin/devices", RegisterDeviceAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("RegisterDevice");
app.MapGet("/api/admin/devices", ListDevicesAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("ListDevices");
app.MapPut("/api/admin/devices/{deviceId:guid}", UpdateDeviceAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("UpdateDevice");
app.MapPatch("/api/admin/devices/{deviceId:guid}", SetDeviceActivationAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("SetDeviceActivation");
app.MapPost("/api/admin/devices/{deviceId:guid}/rotate-secret", RotateDeviceSecretAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("RotateDeviceSecret");
app.MapPost("/api/admin/config", PublishConfigurationAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("PublishConfiguration");
app.MapGet("/api/admin/config", GetConfigurationAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("GetConfiguration");
app.MapGet("/api/edge/config/bootstrap", GetBootstrapAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithName("GetConfigurationBootstrap");

await app.InitializeCoreServiceAsync();
app.Run();

static async Task<IResult> RegisterDeviceAsync(
    RegisterDeviceRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IDeviceManagementService service,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    EnsureCenterAccess(user, request.ExchangeCenterCode);
    var idempotencyKey = context.Request.RequireIdempotencyKey();
    var result = await service.RegisterAsync(
        new RegisterDeviceCommand(
            request.Name,
            request.DeviceType,
            request.LogicalCode,
            request.Owner,
            request.ExchangeCenterCode,
            request.Description,
            idempotencyKey,
            user.Actor(),
            context.CorrelationId()),
        cancellationToken).ConfigureAwait(false);
    return result.IsReplay
        ? Results.Ok(result)
        : Results.Created(
            $"/api/admin/devices?exchangeCenterCode={Uri.EscapeDataString(request.ExchangeCenterCode)}",
            result);
}

static async Task<IResult> ListDevicesAsync(
    string? exchangeCenterCode,
    int? page,
    int? pageSize,
    ClaimsPrincipal user,
    IDeviceQueryService query,
    CancellationToken cancellationToken)
{
    var paging = ValidatePaging(page, pageSize);
    var devices = await query.ListPageAsync(
            user.ToAccessScope(),
            exchangeCenterCode,
            paging.Page,
            paging.PageSize,
            cancellationToken)
        .ConfigureAwait(false);
    return Results.Ok(new PagedResponse<DeviceDto>(
        devices.Items,
        paging.Page,
        paging.PageSize,
        devices.TotalCount));
}

static async Task<IResult> UpdateDeviceAsync(
    Guid deviceId,
    UpdateDeviceRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IDeviceManagementService service,
    IDeviceQueryService query,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    var idempotencyKey = context.Request.RequireIdempotencyKey();
    await EnsureDeviceAccessAsync(deviceId, user, query, cancellationToken).ConfigureAwait(false);
    await service.UpdateAsync(
        new UpdateDeviceCommand(
            deviceId,
            request.Name,
            request.DeviceType,
            request.Owner,
            request.Description,
            idempotencyKey,
            user.Actor(),
            context.CorrelationId()),
        cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
}

static async Task<IResult> SetDeviceActivationAsync(
    Guid deviceId,
    DeviceActivationRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IDeviceManagementService service,
    IDeviceQueryService query,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    var idempotencyKey = context.Request.RequireIdempotencyKey();
    await EnsureDeviceAccessAsync(deviceId, user, query, cancellationToken).ConfigureAwait(false);
    await service.SetActivationAsync(
        deviceId,
        request.Active,
        idempotencyKey,
        user.Actor(),
        context.CorrelationId(),
        cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
}

static async Task<IResult> RotateDeviceSecretAsync(
    Guid deviceId,
    ClaimsPrincipal user,
    HttpContext context,
    IDeviceManagementService service,
    IDeviceQueryService query,
    IConfiguration configuration,
    CancellationToken cancellationToken)
{
    await EnsureDeviceAccessAsync(deviceId, user, query, cancellationToken).ConfigureAwait(false);
    var result = await service.RotateSecretAsync(
        new RotateDeviceSecretCommand(
            deviceId,
            context.Request.RequireIdempotencyKey(),
            user.Actor(),
            context.CorrelationId(),
            ReadSecretRotationGrace(configuration)),
        cancellationToken).ConfigureAwait(false);
    return Results.Ok(result);
}

static async Task<IResult> PublishConfigurationAsync(
    PublishConfigurationRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IConfigurationService service,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(request.PostalCodes);
    EnsureCenterAccess(user, request.ExchangeCenterCode);
    var idempotencyKey = context.Request.RequireIdempotencyKey();
    var shortThreshold = Seconds(request.ShortReadingThresholdSeconds, nameof(request.ShortReadingThresholdSeconds));
    var returnThreshold = Seconds(request.ReturnThresholdSeconds, nameof(request.ReturnThresholdSeconds));
    var maximumOpenBagDuration = Minutes(
        request.MaximumOpenBagMinutes ?? 60,
        nameof(request.MaximumOpenBagMinutes));
    var result = await service.PublishAsync(
        new PublishConfigurationCommand(
            request.ExchangeCenterCode,
            request.PostalCodes,
            shortThreshold,
            returnThreshold,
            request.WeightToleranceKg,
            request.DimensionToleranceCm,
            idempotencyKey,
            user.Actor(),
            context.CorrelationId(),
            request.AutoDispatchEnabled,
            maximumOpenBagDuration),
        cancellationToken).ConfigureAwait(false);
    return Results.Created(
        $"/api/admin/config?exchangeCenterCode={Uri.EscapeDataString(request.ExchangeCenterCode)}",
        result);
}

static async Task<IResult> GetConfigurationAsync(
    string exchangeCenterCode,
    ClaimsPrincipal user,
    IConfigurationQueryService query,
    CancellationToken cancellationToken)
{
    var result = await query.GetCurrentAsync(exchangeCenterCode, user.ToAccessScope(), cancellationToken)
        .ConfigureAwait(false);
    return result is null ? Results.NotFound() : Results.Ok(result);
}

static async Task<IResult> GetBootstrapAsync(
    string? exchangeCenterCode,
    long? currentVersion,
    ClaimsPrincipal user,
    HttpContext context,
    IConfigurationService service,
    CancellationToken cancellationToken)
{
    if (currentVersion < 0)
    {
        throw new CoreValidationException("currentVersion cannot be negative.");
    }

    var center = ResolveBootstrapCenter(exchangeCenterCode, user);
    var result = await service.GetBootstrapAsync(center, cancellationToken).ConfigureAwait(false);
    var etag = ConfigurationEtag(result.ExchangeCenterCode, result.Version);
    context.Response.Headers.ETag = etag;
    context.Response.Headers.CacheControl = "private, no-cache";
    var matchesEtag = MatchesIfNoneMatch(context.Request.Headers.IfNoneMatch, etag);
    if (currentVersion == result.Version || matchesEtag)
    {
        return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return Results.Ok(result);
}

static async Task EnsureDeviceAccessAsync(
    Guid deviceId,
    ClaimsPrincipal user,
    IDeviceQueryService query,
    CancellationToken cancellationToken)
{
    var devices = await query.ListAsync(user.ToAccessScope(), cancellationToken: cancellationToken)
        .ConfigureAwait(false);
    if (!devices.Any(x => x.DeviceId == deviceId))
    {
        throw new CoreNotFoundException("Sorting device does not exist in the caller's access scope.");
    }
}

static string ResolveBootstrapCenter(string? requestedCenter, ClaimsPrincipal user)
{
    var identity = user.AuthenticatedEdge();
    if (!string.IsNullOrWhiteSpace(requestedCenter))
    {
        var center = requestedCenter.Trim();
        if (!string.Equals(center, identity.ExchangeCenterCode, StringComparison.Ordinal))
        {
            throw new CoreAuthorizationException(
                "The requested bootstrap center must match the authenticated Edge center.");
        }

        return center;
    }

    return identity.ExchangeCenterCode;
}

static void EnsureCenterAccess(ClaimsPrincipal user, string centerCode)
{
    var scope = user.ToAccessScope();
    if (!scope.IsGlobal && !scope.AllowedCenterCodes.Contains(centerCode, StringComparer.Ordinal))
    {
        throw new CoreAuthorizationException("The caller is not allowed to manage this exchange center.");
    }
}

static TimeSpan Seconds(long value, string fieldName)
{
    if (value <= 0 || value > TimeSpan.MaxValue.TotalSeconds)
    {
        throw new CoreValidationException($"{fieldName} must be a positive valid duration.");
    }

    return TimeSpan.FromSeconds(value);
}

static TimeSpan Minutes(int value, string fieldName)
{
    if (value is < 1 or > 10_080)
    {
        throw new CoreValidationException($"{fieldName} must be between 1 and 10080 minutes.");
    }

    return TimeSpan.FromMinutes(value);
}

static TimeSpan ReadSecretRotationGrace(IConfiguration configuration)
{
    var seconds = configuration.GetValue(
        "DeviceConfig:SecretRotationGraceSeconds",
        (int)SortingDevice.DefaultSecretRotationGrace.TotalSeconds);
    if (seconds < SortingDevice.MinimumSecretRotationGrace.TotalSeconds ||
        seconds > SortingDevice.MaximumSecretRotationGrace.TotalSeconds)
    {
        throw new CoreValidationException(
            "DeviceConfig:SecretRotationGraceSeconds must be between 60 and 86400 seconds.");
    }

    return TimeSpan.FromSeconds(seconds);
}

static string ConfigurationEtag(string center, long version)
{
    var centerHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(center)))[..16];
    return string.Concat(
        "\"cfg-",
        centerHash,
        "-",
        version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "\"");
}

static bool MatchesIfNoneMatch(Microsoft.Extensions.Primitives.StringValues values, string currentEtag)
{
    foreach (var rawValue in values)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            throw new CoreValidationException("If-None-Match contains an invalid entity tag.");
        }

        foreach (var rawTag in rawValue.Split(',', StringSplitOptions.TrimEntries))
        {
            if (rawTag == "*")
            {
                return true;
            }

            var tag = rawTag.StartsWith("W/", StringComparison.Ordinal) ? rawTag[2..] : rawTag;
            if (tag.Length < 2 || tag[0] != '"' || tag[^1] != '"' ||
                tag.AsSpan(1, tag.Length - 2).Contains('"') ||
                tag.Any(char.IsControl))
            {
                throw new CoreValidationException("If-None-Match contains an invalid entity tag.");
            }

            if (string.Equals(tag, currentEtag, StringComparison.Ordinal))
            {
                return true;
            }
        }
    }

    return false;
}

static (int Page, int PageSize) ValidatePaging(int? page, int? pageSize)
{
    var effectivePage = page ?? 1;
    var effectivePageSize = pageSize ?? 50;
    if (effectivePage <= 0 || effectivePageSize is <= 0 or > 200)
    {
        throw new CoreValidationException("Page must be positive and pageSize must be between 1 and 200.");
    }

    return (effectivePage, effectivePageSize);
}
