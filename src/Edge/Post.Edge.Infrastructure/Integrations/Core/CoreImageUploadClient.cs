using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Resilience;

namespace Post.Edge.Infrastructure.Integrations.Core;

public sealed class CoreImageUploadClient(
    HttpClient httpClient,
    ICoreAccessTokenProvider tokens,
    IOptions<CoreApiOptions> options) : ICoreImageUploadClient
{
    public async Task UploadAsync(CoreImageUpload image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        var attachmentId = DeterministicAttachmentId(image.IdempotencyKey);
        var contentType = string.IsNullOrWhiteSpace(image.MimeType)
            ? "application/octet-stream"
            : image.MimeType.Trim();
        using var presignedResponse = await CoreAuthenticatedSender.SendAsync(
            httpClient,
            tokens,
            () => PresignedRequest(
                options.Value.ImagePresignedPath,
                new
                {
                    EventId = attachmentId,
                    image.ReadingRecordId,
                    image.AttachmentType,
                    ContentType = contentType,
                    ContentLength = (long)image.Content.Length,
                    LifetimeSeconds = options.Value.ImageUploadLifetimeSeconds
                }),
            cancellationToken);
        presignedResponse.EnsureSuccessStatusCode();
        var result = await BoundedHttpResponse.ReadJsonAsync<PresignedResponse>(
                         presignedResponse.Content,
                         options.Value.MaximumResponseBytes,
                         null,
                         cancellationToken)
                     ?? throw new InvalidOperationException("Core image presigned-url response was empty.");
        if (string.IsNullOrWhiteSpace(result.ObjectKey) || result.UploadUrl is null || !result.UploadUrl.IsAbsoluteUri)
            throw new InvalidOperationException("Core returned an invalid image upload URL.");
        var encryptionHeader = result.RequiredHeaders?.SingleOrDefault();
        if (result.RequiredHeaders is null ||
            result.RequiredHeaders.Count != 1 ||
            !string.Equals(
                encryptionHeader?.Key,
                "x-amz-server-side-encryption",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(encryptionHeader?.Value, "AES256", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Core returned an invalid required-header policy for the image upload.");
        }

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, result.UploadUrl)
        {
            Content = new ByteArrayContent(image.Content)
        };
        if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType))
            uploadRequest.Content.Headers.ContentType = mediaType;
        uploadRequest.Content.Headers.ContentLength = image.Content.LongLength;
        uploadRequest.Headers.TryAddWithoutValidation("x-amz-server-side-encryption", "AES256");
        using var uploadResponse = await httpClient.SendAsync(
            uploadRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        uploadResponse.EnsureSuccessStatusCode();

        using var metadataResponse = await CoreAuthenticatedSender.SendAsync(
            httpClient,
            tokens,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, options.Value.ImageMetadataPath)
                {
                    Content = JsonContent.Create(new
                    {
                        EventId = attachmentId,
                        EdgeId = RequireEdgeId(),
                        ExchangeCenterCode = RequireExchangeCenterCode(),
                        image.ParcelBarcode,
                        image.ReadingRecordId,
                        result.ObjectKey,
                        ContentType = contentType,
                        image.AttachmentType,
                        image.IdempotencyKey,
                        CorrelationId = image.ReadId.ToString("D"),
                        ReceivedAt = image.CapturedAt
                    })
                };
                request.Headers.TryAddWithoutValidation("Idempotency-Key", image.IdempotencyKey);
                request.Headers.TryAddWithoutValidation("X-Correlation-ID", image.ReadId.ToString("D"));
                return request;
            },
            cancellationToken);
        metadataResponse.EnsureSuccessStatusCode();
    }

    private static Guid DeterministicAttachmentId(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static HttpRequestMessage PresignedRequest<T>(string path, T payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload)
        };
        EdgeHttpResilience.MarkReplaySafe(request);
        return request;
    }

    private Guid RequireEdgeId() => options.Value.EdgeId != Guid.Empty
        ? options.Value.EdgeId
        : throw new InvalidOperationException("Connected Core image upload requires Edge:CoreApi:EdgeId.");

    private string RequireExchangeCenterCode() => !string.IsNullOrWhiteSpace(options.Value.ExchangeCenterCode)
        ? options.Value.ExchangeCenterCode
        : throw new InvalidOperationException(
            "Connected Core image upload requires Edge:CoreApi:ExchangeCenterCode.");

    private sealed record PresignedResponse(
        string? ObjectKey,
        Uri? UploadUrl,
        DateTimeOffset ExpiresAt,
        IReadOnlyDictionary<string, string>? RequiredHeaders);
}
