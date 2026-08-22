using System.Buffers;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Post.ApiSupport;
using Post.Core.ApiSupport;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.DataIngestion.Api;
using Post.Core.Domain.Common;
using Post.Core.Domain.ValueObjects;
using Post.Core.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
// Ensure the first JSON-body middleware applies the ingestion envelope limit to
// both Content-Length and chunked requests before any larger shared buffering.
builder.Configuration["Api:JsonRequestBodyMaximumBytes"] =
    IngestionPayloadLimits.MaximumRequestBodyBytes.ToString(CultureInfo.InvariantCulture);
builder.AddCoreServiceDefaults("Post.Core.DataIngestion.Api", CoreServiceProfile.DataIngestion);
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<CoreParcelStatus>()));

var app = builder.Build();
app.UsePostApiDefaults();
app.Use(async (context, next) =>
{
    if (!IsIngestionWrite(context.Request))
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    if (context.Request.ContentLength > IngestionPayloadLimits.MaximumRequestBodyBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    var requestSizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (requestSizeFeature is { IsReadOnly: false })
    {
        requestSizeFeature.MaxRequestBodySize = IngestionPayloadLimits.MaximumRequestBodyBytes;
    }

    if (context.Request.ContentLength is not null)
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    var originalBody = context.Request.Body;
    await using var bufferedBody = new MemoryStream();
    var rented = ArrayPool<byte>.Shared.Rent(16_384);
    try
    {
        while (true)
        {
            var read = await originalBody.ReadAsync(rented, context.RequestAborted).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (bufferedBody.Length + read > IngestionPayloadLimits.MaximumRequestBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            await bufferedBody.WriteAsync(rented.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
        }

        bufferedBody.Position = 0;
        context.Request.Body = bufferedBody;
        await next(context).ConfigureAwait(false);
    }
    finally
    {
        context.Request.Body = originalBody;
        ArrayPool<byte>.Shared.Return(rented);
    }
});

app.MapPost("/api/edge/parcel-events", PublishParcelEventAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithMetadata(new RequestSizeLimitAttribute(IngestionPayloadLimits.MaximumRequestBodyBytes))
    .WithName("PublishParcelEvent");
app.MapPost("/api/edge/images/metadata", PublishImageMetadataAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithMetadata(new RequestSizeLimitAttribute(IngestionPayloadLimits.MaximumRequestBodyBytes))
    .WithName("PublishImageMetadata");
app.MapPost("/api/edge/errors", PublishEdgeErrorAsync)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithMetadata(new RequestSizeLimitAttribute(IngestionPayloadLimits.MaximumRequestBodyBytes))
    .WithName("PublishEdgeError");

await app.InitializeCoreServiceAsync();
app.Run();

static async Task<IResult> PublishParcelEventAsync(
    ParcelEventRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IConfiguration configuration,
    IInboundValidationService ingestion,
    IRequestHasher requestHasher,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    ArgumentNullException.ThrowIfNull(request.Payload);
    EnsureRequestIdentity(user, request.EdgeId, request.ExchangeCenterCode);
    _ = new ParcelBarcode(request.Payload.ParcelBarcode);
    var idempotencyKey = ReadIdempotencyKey(context, request.IdempotencyKey);
    var topic = RequireConfiguredTopic(
        request.Topic,
        configuration["Messaging:Topics:ParcelEvents"] ?? "parcel.events.received");
    var correlationId = context.CorrelationId();
    var payload = new ParcelFinalEventCommand(
        request.EventId,
        request.EdgeId,
        request.ExchangeCenterCode,
        idempotencyKey,
        request.Payload.ParcelBarcode,
        request.Payload.FinalStatus,
        correlationId,
        request.OccurredAt);
    var businessFingerprint = requestHasher.Hash(new
    {
        payload.EventId,
        payload.EdgeId,
        payload.ExchangeCenterCode,
        payload.IdempotencyKey,
        payload.ParcelBarcode,
        payload.FinalStatus,
        payload.OccurredAt
    });
    var receipt = await ingestion.ValidateAndPublishAsync(
        new IngestionEventCommand(
            request.EventId,
            topic,
            idempotencyKey,
            correlationId,
            payload,
            request.OccurredAt,
            businessFingerprint),
        cancellationToken).ConfigureAwait(false);
    return Results.Accepted(value: receipt);
}

static async Task<IResult> PublishImageMetadataAsync(
    ImageMetadataRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IConfiguration configuration,
    IInboundValidationService ingestion,
    IRequestHasher requestHasher,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    EnsureRequestIdentity(user, request.EdgeId, request.ExchangeCenterCode);
    _ = new ParcelBarcode(request.ParcelBarcode);
    if (request.ReadingRecordId == Guid.Empty ||
        string.IsNullOrWhiteSpace(request.ObjectKey) ||
        request.ObjectKey.Contains("..", StringComparison.Ordinal))
    {
        throw new CoreValidationException("Valid reading, object key, content type, and attachment type are required.");
    }


    var contentType = AttachmentMetadataRules.NormalizeContentType(request.ContentType);
    var attachmentType = AttachmentMetadataRules.NormalizeAttachmentType(request.AttachmentType);

    var expectedObjectKey = EdgeUploadObjectKey.Create(
        request.EdgeId,
        request.ExchangeCenterCode,
        request.ReadingRecordId,
        request.EventId);
    if (!string.Equals(request.ObjectKey, expectedObjectKey, StringComparison.Ordinal))
    {
        throw new CoreAuthorizationException(
            "The attachment object key does not belong to the authenticated Edge upload namespace.");
    }

    var idempotencyKey = ReadIdempotencyKey(context, request.IdempotencyKey);
    var correlationId = context.CorrelationId();
    var topic = configuration["Messaging:Topics:ImageMetadata"] ?? "image.metadata.received";
    var payload = new ImageMetadataCommand(
        request.EventId,
        request.EdgeId,
        request.ExchangeCenterCode,
        request.ParcelBarcode,
        request.ReadingRecordId,
        request.ObjectKey,
        contentType,
        attachmentType,
        correlationId,
        request.ReceivedAt);
    var businessFingerprint = requestHasher.Hash(new
    {
        payload.EventId,
        payload.EdgeId,
        payload.ExchangeCenterCode,
        payload.ParcelBarcode,
        payload.ReadingRecordId,
        payload.ObjectKey,
        payload.ContentType,
        payload.AttachmentType,
        payload.ReceivedAt
    });
    var receipt = await ingestion.ValidateAndPublishAsync(
        new IngestionEventCommand(
            request.EventId,
            topic,
            idempotencyKey,
            correlationId,
            payload,
            request.ReceivedAt,
            businessFingerprint),
        cancellationToken).ConfigureAwait(false);
    return Results.Accepted(value: receipt);
}

static async Task<IResult> PublishEdgeErrorAsync(
    EdgeErrorRequest request,
    ClaimsPrincipal user,
    HttpContext context,
    IConfiguration configuration,
    IInboundValidationService ingestion,
    IRequestHasher requestHasher,
    CancellationToken cancellationToken)
{
    ArgumentNullException.ThrowIfNull(request);
    EnsureRequestIdentity(user, request.EdgeId, request.ExchangeCenterCode);
    if (request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
    {
        throw new CoreValidationException("Error event payload is required.");
    }

    var idempotencyKey = ReadIdempotencyKey(context, request.IdempotencyKey);
    var topic = RequireConfiguredTopic(
        request.Topic,
        configuration["Messaging:Topics:EdgeErrors"] ?? "edge.integration-error.v1");
    var correlationId = context.CorrelationId();
    var payload = new
    {
        request.EventId,
        request.EdgeId,
        request.ExchangeCenterCode,
        Error = request.Payload,
        CorrelationId = correlationId,
        request.OccurredAt
    };
    var businessFingerprint = requestHasher.Hash(new
    {
        request.EventId,
        request.EdgeId,
        request.ExchangeCenterCode,
        Topic = topic,
        IdempotencyKey = idempotencyKey,
        Error = request.Payload,
        request.OccurredAt
    });
    var receipt = await ingestion.ValidateAndPublishAsync(
        new IngestionEventCommand(
            request.EventId,
            topic,
            idempotencyKey,
            correlationId,
            payload,
            request.OccurredAt,
            businessFingerprint),
        cancellationToken).ConfigureAwait(false);
    return Results.Accepted(value: receipt);
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

static void EnsureRequestIdentity(ClaimsPrincipal user, Guid edgeId, string exchangeCenterCode)
{
    var identity = user.AuthenticatedEdge();
    if (identity.EdgeId != edgeId ||
        !string.Equals(identity.ExchangeCenterCode, exchangeCenterCode, StringComparison.Ordinal))
    {
        throw new CoreAuthorizationException(
            "The request Edge identity and exchange center must match the authenticated Edge token.");
    }
}

static string RequireConfiguredTopic(string? suppliedTopic, string configuredTopic)
{
    if (string.IsNullOrWhiteSpace(configuredTopic))
    {
        throw new InvalidOperationException("The ingestion topic is not configured.");
    }

    if (!string.IsNullOrWhiteSpace(suppliedTopic) &&
        !string.Equals(suppliedTopic.Trim(), configuredTopic, StringComparison.Ordinal))
    {
        throw new CoreValidationException("The supplied event topic is not valid for this endpoint.");
    }

    return configuredTopic;
}

static bool IsIngestionWrite(HttpRequest request) =>
    HttpMethods.IsPost(request.Method) &&
    (request.Path == "/api/edge/parcel-events" ||
     request.Path == "/api/edge/images/metadata" ||
     request.Path == "/api/edge/errors");
