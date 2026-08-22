using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Polly;
using Polly.Timeout;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Resilience;

internal static class EdgeHttpResilience
{
    private static readonly HttpRequestOptionsKey<bool> ReplaySafeKey = new("Post.Edge.ReplaySafe");

    internal static IHttpClientBuilder AddStandard(
        IHttpClientBuilder builder,
        EdgeResilienceOptions settings,
        TimeSpan totalTimeout)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(settings);
        var attemptTimeout = settings.HttpAttemptTimeout < totalTimeout
            ? settings.HttpAttemptTimeout
            : TimeSpan.FromTicks(totalTimeout.Ticks / 2);
        builder.AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = totalTimeout;
            options.AttemptTimeout.Timeout = attemptTimeout;
            options.Retry.MaxRetryAttempts = settings.HttpMaxRetryAttempts;
            options.Retry.Delay = settings.HttpRetryBaseDelay;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.ShouldRetryAfterHeader = true;
            options.Retry.ShouldHandle = static arguments =>
            {
                var request = arguments.Context.GetRequestMessage();
                var shouldRetry = request is not null &&
                                  IsReplaySafe(request) &&
                                  IsTransient(arguments.Outcome);
                return ValueTask.FromResult(shouldRetry);
            };
            options.CircuitBreaker.FailureRatio = settings.HttpCircuitFailureRatio;
            options.CircuitBreaker.MinimumThroughput = settings.HttpCircuitMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = settings.HttpCircuitSamplingDuration;
            options.CircuitBreaker.BreakDuration = settings.HttpCircuitBreakDuration;
        });
        return builder;
    }

    internal static void MarkReplaySafe(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(ReplaySafeKey, true);
    }

    internal static bool IsReplaySafe(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Method == HttpMethod.Get ||
               request.Method == HttpMethod.Head ||
               request.Method == HttpMethod.Options ||
               request.Method == HttpMethod.Trace ||
               request.Method == HttpMethod.Put ||
               request.Method == HttpMethod.Delete ||
               request.Headers.Contains("Idempotency-Key") ||
               request.Options.TryGetValue(ReplaySafeKey, out var marked) && marked;
    }

    private static bool IsTransient(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is HttpRequestException or TimeoutRejectedException ||
        outcome.Result is { } response &&
        (response.StatusCode == HttpStatusCode.RequestTimeout ||
         response.StatusCode == HttpStatusCode.TooManyRequests ||
         (int)response.StatusCode >= 500);
}
