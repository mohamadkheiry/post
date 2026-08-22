using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Post.ApiSupport;
using Post.Core.ApiSupport;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.EdgeHealth.Api;
using Post.Core.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddCoreServiceDefaults("Post.Core.EdgeHealth.Api", CoreServiceProfile.EdgeHealth);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<OperationalEventType>());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<EdgeConnectionState>());
});

var app = builder.Build();
app.UsePostApiDefaults();

app.MapPost("/api/edge/registration", RegisterEdgeAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithName("RegisterEdge");
app.MapPost("/api/edge/heartbeat", RecordHeartbeatAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithName("RecordEdgeHeartbeat");
app.MapPost("/api/edge/events", RecordOperationalEventAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithName("RecordEdgeOperationalEvent");
app.MapGet("/api/admin/edges/health", ListEdgeHealthAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("ListEdgeHealth");

await app.InitializeCoreServiceAsync();
app.Run();

static async Task<IResult> RegisterEdgeAsync(
    RegisterEdgeRequest request,
    ClaimsPrincipal user,
    IEdgeHealthService service,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    var identity = user.AuthenticatedEdge();
    EnsureRequestIdentity(identity, request.EdgeId, request.ExchangeCenterCode);
    await service.RegisterAsync(
        new RegisterEdgeCommand(
            request.EdgeId,
            request.ExchangeCenterCode,
            request.SoftwareVersion,
            request.ConfigurationVersion,
            request.RegisteredAt),
        cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
}

static async Task<IResult> RecordHeartbeatAsync(
    EdgeHeartbeatRequest request,
    ClaimsPrincipal user,
    IEdgeHealthService service,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    await EnsureEdgeAccessAsync(request.EdgeId, user, service, cancellationToken).ConfigureAwait(false);
    await service.RecordHeartbeatAsync(
        new EdgeHeartbeatCommand(
            request.EdgeId,
            request.HeartbeatAt,
            request.SoftwareVersion,
            request.ConfigurationVersion,
            request.AvailableDiskBytes,
            request.PendingCount,
            request.FailedCount,
            request.DeadLetterCount,
            request.LastSuccessfulSync),
        cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
}

static async Task<IResult> RecordOperationalEventAsync(
    OperationalEventRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IEdgeHealthService service,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    var idempotencyKey = ReadIdempotencyKey(context, request.IdempotencyKey);
    await EnsureEdgeAccessAsync(request.EdgeId, user, service, cancellationToken).ConfigureAwait(false);

    await service.RecordEventAsync(
        new RecordOperationalEventCommand(
            request.EventType,
            request.EdgeId,
            request.DeviceId,
            idempotencyKey,
            context.CorrelationId(),
            request.OccurredAt),
        cancellationToken).ConfigureAwait(false);
    return Results.NoContent();
}

static async Task<IResult> ListEdgeHealthAsync(
    int? page,
    int? pageSize,
    ClaimsPrincipal user,
    IEdgeHealthService service,
    CancellationToken cancellationToken)
{
    var paging = ValidatePaging(page, pageSize);
    var result = await service.ListPageAsync(
        user.ToAccessScope(),
        paging.Page,
        paging.PageSize,
        cancellationToken).ConfigureAwait(false);
    return Results.Ok(new PagedResponse<EdgeHealthDto>(
        result.Items,
        paging.Page,
        paging.PageSize,
        result.TotalCount));
}

static async Task EnsureEdgeAccessAsync(
    Guid edgeId,
    ClaimsPrincipal user,
    IEdgeHealthService service,
    CancellationToken cancellationToken)
{
    var identity = user.AuthenticatedEdge();
    if (identity.EdgeId != edgeId)
    {
        throw new CoreAuthorizationException("The authenticated Edge identifier does not match the request.");
    }

    var status = await service.FindAsync(edgeId, cancellationToken).ConfigureAwait(false);
    if (status is null)
    {
        throw new CoreNotFoundException("Edge registration does not exist.");
    }

    EnsureRequestIdentity(identity, edgeId, status.ExchangeCenterCode);
}

static void EnsureRequestIdentity(
    AuthenticatedEdgeIdentity identity,
    Guid edgeId,
    string exchangeCenterCode)
{
    if (identity.EdgeId != edgeId ||
        !string.Equals(identity.ExchangeCenterCode, exchangeCenterCode?.Trim(), StringComparison.Ordinal))
    {
        throw new CoreAuthorizationException("The caller is not allowed to manage Edge health for this center.");
    }
}

static string ReadIdempotencyKey(HttpContext context, string? bodyValue)
{
    var headerValue = context.Request.RequireIdempotencyKey();
    if (!string.IsNullOrWhiteSpace(bodyValue) &&
        !string.Equals(headerValue, bodyValue.Trim(), StringComparison.Ordinal))
    {
        throw new CoreValidationException("Body and Idempotency-Key header values must match.");
    }

    return headerValue;
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
