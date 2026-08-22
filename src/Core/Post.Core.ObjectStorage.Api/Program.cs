using System.Security.Claims;
using Post.ApiSupport;
using Post.Core.ApiSupport;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Infrastructure;
using Post.Core.ObjectStorage.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddCoreServiceDefaults("Post.Core.ObjectStorage.Api", CoreServiceProfile.ObjectStorage);

var app = builder.Build();
app.UsePostApiDefaults();

app.MapPost("/api/edge/images/presigned-url", CreatePresignedUrl)
    .RequireAuthorization(PostAuthorizationPolicies.Edge)
    .WithName("CreateImageUploadUrl");

await app.InitializeCoreServiceAsync();
app.Run();

static IResult CreatePresignedUrl(
    PresignedUrlRequest request,
    ClaimsPrincipal user,
    IConfiguration configuration,
    IPresignedUrlService service)
{
    ArgumentNullException.ThrowIfNull(request);
    var maximumSeconds = configuration.GetValue("ObjectStorage:MaximumPresignLifetimeSeconds", 900);
    var maximumUploadBytes = configuration.GetValue<long>("ObjectStorage:MaximumUploadBytes", 4_000_000);
    if (maximumUploadBytes <= 0)
    {
        throw new InvalidOperationException("ObjectStorage:MaximumUploadBytes must be positive.");
    }

    if (maximumSeconds is <= 0 or > 604800 ||
        request.LifetimeSeconds <= 0 ||
        request.LifetimeSeconds > maximumSeconds)
    {
        throw new CoreValidationException(
            $"lifetimeSeconds must be between 1 and {maximumSeconds} seconds.");
    }

    if (request.ContentLength <= 0 || request.ContentLength > maximumUploadBytes)
    {
        throw new CoreValidationException(
            $"contentLength must be between 1 and {maximumUploadBytes} bytes.");
    }

    var identity = user.AuthenticatedEdge();
    var result = service.Create(new PresignedUrlCommand(
        request.EventId,
        identity.EdgeId,
        identity.ExchangeCenterCode,
        request.ReadingRecordId,
        request.AttachmentType,
        request.ContentType,
        request.ContentLength,
        TimeSpan.FromSeconds(request.LifetimeSeconds)));
    return Results.Ok(result);
}
