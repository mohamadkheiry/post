using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using Post.ApiSupport;
using Post.Core.ApiSupport;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Infrastructure;
using Post.Core.ParcelLifecycle.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddCoreServiceDefaults("Post.Core.ParcelLifecycle.Api", CoreServiceProfile.ParcelLifecycle);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<CoreParcelStatus>());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<DeviceParcelStatus>());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<PhysicalAttributeKind>());
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<BagState>());
});

var app = builder.Build();
app.UsePostApiDefaults();

app.MapPost("/api/edge/parcels/inbound-query", ExecuteInboundQueryAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithName("ExecuteInboundParcelQuery");
app.MapPost("/api/edge/bags", StoreBagAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithName("StoreBag");
app.MapPost("/api/edge/dispatches", StoreDispatchAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithName("StoreDispatch");
app.MapGet("/api/admin/parcels/{barcode}", GetParcelAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("GetParcel");
app.MapGet("/api/admin/parcels", SearchParcelsAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("SearchParcels");
app.MapGet("/api/admin/bags/{barcode}", GetBagAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("GetBag");
app.MapGet("/api/admin/dispatches/{dispatchId}", GetDispatchAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Administrator)
    .WithName("GetDispatch");

await app.InitializeCoreServiceAsync();
app.Run();

static async Task<IResult> ExecuteInboundQueryAsync(
    InboundQueryRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IInboundQueryService service,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(request.Barcodes);
    ArgumentNullException.ThrowIfNull(request.PhysicalAttributes);
    var identity = user.AuthenticatedEdge();
    EnsureEdgeCenter(identity, request.ExchangeCenterCode);
    var idempotencyKey = ReadIdempotencyKey(context, request.IdempotencyKey);
    var result = await service.ExecuteAsync(
        new InboundQueryCommand(
            idempotencyKey,
            request.Barcodes,
            request.ParcelType,
            request.ServiceType,
            request.OriginCenterCode,
            request.DestinationCenterCode,
            request.DeviceId,
            request.ExchangeCenterCode,
            request.PhysicalAttributes,
            request.EdgeEventTime,
            context.CorrelationId()),
        cancellationToken).ConfigureAwait(false);
    return Results.Ok(result);
}

static async Task<IResult> StoreBagAsync(
    BagRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IBagStorageService service,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(request.MemberBarcodes);
    var identity = user.AuthenticatedEdge();
    EnsureEdgeCenter(identity, request.OriginCenterCode);
    var idempotencyKey = ReadIdempotencyKey(context, request.IdempotencyKey);
    var result = await service.StoreAsync(
        new BagStoreCommand(
            idempotencyKey,
            request.BagBarcode,
            request.OriginCenterCode,
            request.DestinationCenterCode,
            request.CreatedByDeviceId,
            request.MemberBarcodes,
            request.SealNumber,
            request.TransportType,
            request.ClosedAt,
            context.CorrelationId()),
        cancellationToken).ConfigureAwait(false);
    return result.IsReplay
        ? Results.Ok(result)
        : Results.Created($"/api/admin/bags/{Uri.EscapeDataString(result.Identifier)}", result);
}

static async Task<IResult> StoreDispatchAsync(
    DispatchRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IDispatchStorageService service,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(request.BagBarcodes);
    var identity = user.AuthenticatedEdge();
    var idempotencyKey = ReadIdempotencyKey(context, request.IdempotencyKey);
    var result = await service.StoreAsync(
        new DispatchStoreCommand(
            idempotencyKey,
            request.DispatchId,
            request.DestinationCenterCode,
            request.BagBarcodes,
            request.ClosedAt,
            context.CorrelationId()),
        new AccessScope(false, [identity.ExchangeCenterCode]),
        cancellationToken).ConfigureAwait(false);
    return result.IsReplay
        ? Results.Ok(result)
        : Results.Created($"/api/admin/dispatches/{Uri.EscapeDataString(result.Identifier)}", result);
}

static async Task<IResult> GetParcelAsync(
    string barcode,
    ClaimsPrincipal user,
    IParcelTrackingQueryService query,
    CancellationToken cancellationToken)
{
    var parcel = await query.FindAsync(barcode, user.ToAccessScope(), cancellationToken).ConfigureAwait(false);
    return parcel is null ? Results.NotFound() : Results.Ok(parcel);
}

static async Task<IResult> SearchParcelsAsync(
    int? page,
    int? pageSize,
    string? barcode,
    ClaimsPrincipal user,
    IParcelTrackingQueryService query,
    CancellationToken cancellationToken)
{
    var paging = ValidatePaging(page, pageSize);
    var parcels = await query.SearchPageAsync(
        user.ToAccessScope(),
        barcode,
        paging.Page,
        paging.PageSize,
        cancellationToken).ConfigureAwait(false);
    return Results.Ok(new PagedResponse<ParcelTrackingDto>(
        parcels.Items,
        paging.Page,
        paging.PageSize,
        parcels.TotalCount));
}

static async Task<IResult> GetBagAsync(
    string barcode,
    ClaimsPrincipal user,
    IBagDispatchQueryService query,
    CancellationToken cancellationToken)
{
    var bag = await query.FindBagAsync(barcode, user.ToAccessScope(), cancellationToken).ConfigureAwait(false);
    return bag is null ? Results.NotFound() : Results.Ok(bag);
}

static async Task<IResult> GetDispatchAsync(
    string dispatchId,
    ClaimsPrincipal user,
    IBagDispatchQueryService query,
    CancellationToken cancellationToken)
{
    var dispatch = await query.FindDispatchAsync(dispatchId, user.ToAccessScope(), cancellationToken)
        .ConfigureAwait(false);
    return dispatch is null ? Results.NotFound() : Results.Ok(dispatch);
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

static void EnsureEdgeCenter(AuthenticatedEdgeIdentity identity, string centerCode)
{
    if (!string.Equals(identity.ExchangeCenterCode, centerCode?.Trim(), StringComparison.Ordinal))
    {
        throw new CoreAuthorizationException("The caller is not allowed to write data for this exchange center.");
    }
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
