using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Post.BuildingBlocks.Correlation;

/// <summary>Validates or creates an HTTP correlation id and exposes it through the request scope.</summary>
public sealed class CorrelationContextMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Initializes the middleware.</summary>
    public CorrelationContextMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    /// <summary>Processes one request.</summary>
    public async Task InvokeAsync(HttpContext httpContext, ICorrelationContext correlationContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(correlationContext);

        var supplied = httpContext.Request.Headers.TryGetValue(CorrelationId.HeaderName, out StringValues values)
            ? values.FirstOrDefault()
            : null;
        var correlationId = CorrelationId.NormalizeOrCreate(supplied);

        httpContext.TraceIdentifier = correlationId;
        httpContext.Response.Headers[CorrelationId.HeaderName] = correlationId;
        httpContext.Response.OnStarting(static state =>
        {
            var (response, value) = ((HttpResponse Response, string Value))state;
            response.Headers[CorrelationId.HeaderName] = value;
            return Task.CompletedTask;
        }, (httpContext.Response, correlationId));

        using (correlationContext.Push(correlationId))
        {
            await _next(httpContext).ConfigureAwait(false);
        }
    }
}
