using System.Buffers;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Api;

internal sealed class EdgeAdministratorRequestBodyLimitMiddleware(
    RequestDelegate next,
    IOptions<EdgeLocalAdministratorOptions> options)
{
    private readonly long _maximumBytes = options.Value.MaximumRequestBodyBytes;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!IsAdministratorWrite(context.Request))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength > _maximumBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        var requestSizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (requestSizeFeature is { IsReadOnly: false })
            requestSizeFeature.MaxRequestBodySize = _maximumBytes;

        if (context.Request.ContentLength is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Request.Body;
        await using var boundedBody = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(8_192);
        try
        {
            while (true)
            {
                var read = await originalBody.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                if (read == 0) break;
                if (boundedBody.Length + read > _maximumBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    return;
                }

                await boundedBody.WriteAsync(
                    buffer.AsMemory(0, read),
                    context.RequestAborted).ConfigureAwait(false);
            }

            boundedBody.Position = 0;
            context.Request.Body = boundedBody;
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Request.Body = originalBody;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsAdministratorWrite(HttpRequest request) =>
        request.Path.StartsWithSegments("/admin", StringComparison.OrdinalIgnoreCase) &&
        (HttpMethods.IsPost(request.Method) ||
         HttpMethods.IsPut(request.Method) ||
         HttpMethods.IsPatch(request.Method));
}
