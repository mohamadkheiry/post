using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Post.ApiSupport;

public sealed class PostExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<PostExceptionHandler> logger) : IExceptionHandler
{
    private static readonly Action<ILogger, string, string, Exception?> UnhandledFailure =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(5000, nameof(UnhandledFailure)),
            "Unhandled request failure. TraceId: {TraceId}; Type: {ExceptionType}");

    private static readonly Action<ILogger, int, string, string, Exception?> RequestRejected =
        LoggerMessage.Define<int, string, string>(
            LogLevel.Warning,
            new EventId(4000, nameof(RequestRejected)),
            "Request rejected with {StatusCode}. TraceId: {TraceId}; Type: {ExceptionType}");

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var mapping = Map(exception);
        if (mapping.Status >= StatusCodes.Status500InternalServerError)
        {
            // Dependency exceptions routinely contain URLs, response bodies, or credentials.
            // Preserve correlation and type without handing raw exception text/stack to providers.
            UnhandledFailure(logger, httpContext.TraceIdentifier, exception.GetType().Name, null);
        }
        else
        {
            RequestRejected(
                logger,
                mapping.Status,
                httpContext.TraceIdentifier,
                exception.GetType().Name,
                null);
        }

        httpContext.Response.StatusCode = mapping.Status;
        var problem = new ProblemDetails
        {
            Status = mapping.Status,
            Title = mapping.Title,
            Detail = mapping.ExposeDetail ? exception.Message : "The request could not be completed.",
            Type = $"https://httpstatuses.io/{mapping.Status}",
            Instance = httpContext.Request.Path
        };
        problem.Extensions["code"] = mapping.Code;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem
        }).ConfigureAwait(false);
    }

    private static ExceptionMapping Map(Exception exception) => exception.GetType().Name switch
    {
        "CoreValidationException" or "DomainRuleException" or "DomainException" =>
            new(StatusCodes.Status400BadRequest, "Request validation failed", "validation.failed", true),
        "CorePayloadTooLargeException" =>
            new(StatusCodes.Status413PayloadTooLarge, "Request payload too large", "request.too_large", true),
        "CoreNotFoundException" =>
            new(StatusCodes.Status404NotFound, "Resource not found", "resource.not_found", true),
        "CoreConflictException" or "UniqueConstraintViolationException" =>
            new(StatusCodes.Status409Conflict, "Request conflicts with current state", "state.conflict", true),
        "CoreAuthorizationException" =>
            new(StatusCodes.Status403Forbidden, "Access denied", "authorization.denied", true),
        "CoreDependencyUnavailableException" =>
            new(StatusCodes.Status503ServiceUnavailable, "Dependency unavailable", "dependency.unavailable", false),
        _ when exception is ArgumentException or FormatException or System.Text.Json.JsonException =>
            new(StatusCodes.Status400BadRequest, "Request validation failed", "validation.failed", true),
        _ => new(StatusCodes.Status500InternalServerError, "Unexpected server error", "server.error", false)
    };

    private sealed record ExceptionMapping(
        int Status,
        string Title,
        string Code,
        bool ExposeDetail);
}
