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
using Post.Core.OperationalResult.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddCoreServiceDefaults("Post.Core.OperationalResult.Api", CoreServiceProfile.OperationalResult);
builder.Services.Configure<JsonOptions>(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<OperationalFinalStatus>()));

var app = builder.Build();
app.UsePostApiDefaults();

app.MapPost("/api/edge/operational-results", StoreOperationalResultAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithName("StoreOperationalResult");

await app.InitializeCoreServiceAsync();
app.Run();

static async Task<IResult> StoreOperationalResultAsync(
    OperationalResultRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IOperationalResultService service,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    var identity = user.AuthenticatedEdge();
    if (identity.EdgeId != request.EdgeId ||
        !string.Equals(identity.ExchangeCenterCode, request.ExchangeCenterCode, StringComparison.Ordinal))
    {
        throw new CoreAuthorizationException(
            "The operational result Edge identity and exchange center must match the authenticated Edge token.");
    }

    var idempotencyKey = ReadIdempotencyKey(context, request.IdempotencyKey);
    var result = await service.StoreAsync(
        new OperationalResultCommand(
            request.EdgeId,
            request.ExchangeCenterCode,
            idempotencyKey,
            context.CorrelationId(),
            request.ParcelBarcode,
            request.CallResult,
            request.Success,
            request.ErrorCode,
            request.ErrorMessage,
            request.CalledAt,
            request.RespondedAt,
            request.Attempts,
            request.FinalStatus),
        cancellationToken).ConfigureAwait(false);
    return result.IsReplay
        ? Results.Ok(result)
        : Results.Json(result, statusCode: StatusCodes.Status201Created);
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
