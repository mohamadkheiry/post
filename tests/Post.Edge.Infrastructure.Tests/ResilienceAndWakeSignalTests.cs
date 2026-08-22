using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Polly.CircuitBreaker;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Resilience;
using Post.Edge.Infrastructure.Workers;

namespace Post.Edge.Infrastructure.Tests;

public sealed class ResilienceAndWakeSignalTests
{
    [Fact]
    public void HttpReplaySafetyRequiresSafeVerbIdempotencyKeyOrExplicitMarker()
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, "https://edge.test");
        using var post = new HttpRequestMessage(HttpMethod.Post, "https://edge.test");
        using var idempotentPost = new HttpRequestMessage(HttpMethod.Post, "https://edge.test");
        idempotentPost.Headers.TryAddWithoutValidation("Idempotency-Key", "operation-1");
        using var markedPost = new HttpRequestMessage(HttpMethod.Post, "https://edge.test");
        EdgeHttpResilience.MarkReplaySafe(markedPost);

        Assert.True(EdgeHttpResilience.IsReplaySafe(get));
        Assert.False(EdgeHttpResilience.IsReplaySafe(post));
        Assert.True(EdgeHttpResilience.IsReplaySafe(idempotentPost));
        Assert.True(EdgeHttpResilience.IsReplaySafe(markedPost));
    }

    [Fact]
    public async Task HttpPolicyRetriesOnlyReplaySafeTransientResponses()
    {
        var replaySafeHandler = new CountingHandler(call =>
            new HttpResponseMessage(call == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        await using var replaySafeProvider = BuildHttpProvider(replaySafeHandler, retryAttempts: 1);
        using var replaySafeClient = replaySafeProvider.GetRequiredService<IHttpClientFactory>().CreateClient("edge-test");
        using var replaySafeRequest = new HttpRequestMessage(HttpMethod.Post, "https://edge.test/resource");
        EdgeHttpResilience.MarkReplaySafe(replaySafeRequest);
        using var replaySafeResponse = await replaySafeClient.SendAsync(replaySafeRequest, TestContext.Current.CancellationToken);

        var unsafeHandler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await using var unsafeProvider = BuildHttpProvider(unsafeHandler, retryAttempts: 2);
        using var unsafeClient = unsafeProvider.GetRequiredService<IHttpClientFactory>().CreateClient("edge-test");
        using var unsafeResponse = await unsafeClient.PostAsync(new Uri("https://edge.test/resource"), new StringContent("payload"), TestContext.Current.CancellationToken);

        var validationHandler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await using var validationProvider = BuildHttpProvider(validationHandler, retryAttempts: 2);
        using var validationClient = validationProvider.GetRequiredService<IHttpClientFactory>().CreateClient("edge-test");
        using var validationRequest = new HttpRequestMessage(HttpMethod.Get, "https://edge.test/resource");
        using var validationResponse = await validationClient.SendAsync(validationRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, replaySafeResponse.StatusCode);
        Assert.Equal(2, replaySafeHandler.CallCount);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unsafeResponse.StatusCode);
        Assert.Equal(1, unsafeHandler.CallCount);
        Assert.Equal(HttpStatusCode.BadRequest, validationResponse.StatusCode);
        Assert.Equal(1, validationHandler.CallCount);
    }

    [Fact]
    public async Task HttpCircuitOpensWithoutIoAndRecoversAfterBreakDuration()
    {
        var handler = new CountingHandler(call => new HttpResponseMessage(
            call <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        await using var provider = BuildHttpProvider(
            handler,
            retryAttempts: 1,
            minimumThroughput: 2,
            breakDuration: TimeSpan.FromMilliseconds(500));
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("edge-test");

        using (var first = await client.PostAsync(new Uri("https://edge.test/first"), null, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        using (var second = await client.PostAsync(new Uri("https://edge.test/second"), null, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        await Assert.ThrowsAnyAsync<BrokenCircuitException>(() =>
            client.PostAsync(new Uri("https://edge.test/open"), null, TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.CallCount);

        await Task.Delay(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken);
        using var recovered = await client.PostAsync(new Uri("https://edge.test/recovered"), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task SqlPolicyBoundsRetriesOpensCircuitAndRecovers()
    {
        var clock = new MutableClock();
        var settings = Microsoft.Extensions.Options.Options.Create(new EdgeResilienceOptions
        {
            SqlMaxRetryAttempts = 2,
            SqlRetryBaseDelay = TimeSpan.FromMilliseconds(1),
            SqlCircuitFailureThreshold = 2,
            SqlCircuitBreakDuration = TimeSpan.FromSeconds(1)
        });
        var policy = new PostalSqlResilienceExecutor(settings, clock);
        var retryCalls = 0;
        var result = await policy.ExecuteAsync(_ => ++retryCalls < 3
                ? Task.FromException<int>(new TimeoutException())
                : Task.FromResult(7), retrySafe: true, TestContext.Current.CancellationToken);
        Assert.Equal(7, result);
        Assert.Equal(3, retryCalls);

        var unsafeCalls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => policy.ExecuteAsync<int>(_ =>
            {
                unsafeCalls++;
                return Task.FromException<int>(new TimeoutException());
            }, retrySafe: false, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TimeoutException>(() => policy.ExecuteAsync<int>(_ => Task.FromException<int>(new TimeoutException()), retrySafe: false, TestContext.Current.CancellationToken));
        Assert.Equal(1, unsafeCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync(_ => Task.FromResult(1), retrySafe: true, TestContext.Current.CancellationToken));

        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        Assert.Equal(9, await policy.ExecuteAsync(_ => Task.FromResult(9), retrySafe: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SqlHalfOpenCancellationReleasesProbeForTheNextRecoveryAttempt()
    {
        var clock = new MutableClock();
        var policy = new PostalSqlResilienceExecutor(
            Microsoft.Extensions.Options.Options.Create(new EdgeResilienceOptions
            {
                SqlMaxRetryAttempts = 1,
                SqlRetryBaseDelay = TimeSpan.FromMilliseconds(1),
                SqlCircuitFailureThreshold = 1,
                SqlCircuitBreakDuration = TimeSpan.FromSeconds(1)
            }),
            clock);
        await Assert.ThrowsAsync<TimeoutException>(() => policy.ExecuteAsync<int>(
            _ => Task.FromException<int>(new TimeoutException()),
            retrySafe: false,
            TestContext.Current.CancellationToken));
        clock.UtcNow = clock.UtcNow.AddSeconds(2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => policy.ExecuteAsync<int>(
            token => Task.FromCanceled<int>(token),
            retrySafe: true,
            cancellation.Token));

        Assert.Equal(11, await policy.ExecuteAsync(
            _ => Task.FromResult(11),
            retrySafe: true,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WakeSignalCoalescesBurstsFallsBackToPollingAndPropagatesCancellation()
    {
        var signal = new OutboxWakeSignal();
        signal.Notify();
        signal.Notify();
        var immediate = signal.WaitAsync(TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        Assert.Same(
            immediate,
            await Task.WhenAny(immediate));
        await immediate;

        var stopwatch = Stopwatch.StartNew();
        await signal.WaitAsync(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(10));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            signal.WaitAsync(TimeSpan.FromMinutes(1), cancellation.Token));
    }

    [Fact]
    public async Task OutboxSignalsOnlyAfterDurableCommitAndRetainsTheRow()
    {
        var signal = new RecordingWakeSignal();
        await using var host = await InfrastructureTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IOutboxWakeSignal>();
            services.AddSingleton<IOutboxWakeSignal>(signal);
        });
        var idempotencyKey = $"wake:{Guid.NewGuid():N}";
        await using var scope = host.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
        writer.Enqueue(new OutboxMessage(
            Guid.NewGuid(),
            "Core",
            "WakeProof",
            "{}",
            idempotencyKey,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch));
        Assert.Equal(0, signal.NotificationCount);
        await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, signal.NotificationCount);
        Assert.True(await scope.ServiceProvider.GetRequiredService<IOutboxStatus>()
            .IsPendingAsync(idempotencyKey, TestContext.Current.CancellationToken));
    }

    private static ServiceProvider BuildHttpProvider(
        HttpMessageHandler handler,
        int retryAttempts,
        int minimumThroughput = 100,
        TimeSpan? breakDuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddHttpClient("edge-test")
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        EdgeHttpResilience.AddStandard(builder, new EdgeResilienceOptions
        {
            HttpMaxRetryAttempts = retryAttempts,
            HttpRetryBaseDelay = TimeSpan.FromMilliseconds(1),
            HttpAttemptTimeout = TimeSpan.FromSeconds(1),
            HttpCircuitFailureRatio = 0.5,
            HttpCircuitMinimumThroughput = minimumThroughput,
            HttpCircuitSamplingDuration = TimeSpan.FromSeconds(2),
            HttpCircuitBreakDuration = breakDuration ?? TimeSpan.FromSeconds(1)
        }, TimeSpan.FromSeconds(3));
        return services.BuildServiceProvider();
    }

    private sealed class CountingHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private int _callCount;
        internal int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = responseFactory(Interlocked.Increment(ref _callCount));
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private sealed class RecordingWakeSignal : IOutboxWakeSignal
    {
        internal int NotificationCount { get; private set; }

        public void Notify() => NotificationCount++;

        public Task WaitAsync(TimeSpan pollingFallback, CancellationToken cancellationToken) =>
            Task.Delay(pollingFallback, cancellationToken);
    }
}
