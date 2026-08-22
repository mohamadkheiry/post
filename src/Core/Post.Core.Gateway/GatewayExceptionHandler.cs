using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Post.Core.Gateway;

/// <summary>Converts unexpected gateway failures into a correlation-safe public response.</summary>
public sealed class GatewayExceptionHandler(ILogger<GatewayExceptionHandler> logger) : IExceptionHandler
{
    private static readonly JsonSerializerOptions ProblemJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, string, string, Exception?> LogGatewayFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(5000, nameof(LogGatewayFailure)),
            "Unhandled gateway failure. TraceId: {TraceId}; Type: {ExceptionType}");

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var traceId = httpContext.TraceIdentifier;
        // Never pass the exception object to a logging provider: upstream exceptions can
        // contain URLs, response bodies, headers, or credentials in their causal chain.
        LogGatewayFailure(logger, traceId, exception.GetType().Name, null);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Gateway request failed",
            Detail = "The gateway could not complete the request."
        };
        problem.Extensions["code"] = "gateway.error";
        problem.Extensions["traceId"] = traceId;

        httpContext.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(
                httpContext.Response.Body,
                problem,
                ProblemJsonOptions,
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}
