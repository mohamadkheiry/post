using System.Net;
using System.Text;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Integrations.Core;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Tests;

public sealed class CoreAccessTokenProviderTests
{
    [Fact]
    public async Task ProviderCachesUntilRefreshSkewThenRenews()
    {
        var clock = new MutableClock();
        var handler = new DelegateHttpHandler((_, attempt, _) => Task.FromResult(
            Json(HttpStatusCode.OK, $$"""{"accessToken":"token-{{attempt}}","expiresIn":600}""")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://core.test") };
        using var provider = new CoreAccessTokenProvider(http, Options(), clock);

        var first = await provider.GetAsync(TestContext.Current.CancellationToken);
        var cached = await provider.GetAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromMinutes(9));
        var refreshed = await provider.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("token-1", first);
        Assert.Equal(first, cached);
        Assert.Equal("token-2", refreshed);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ProviderCollapsesConcurrentRefreshToOneTokenRequest()
    {
        var handler = new DelegateHttpHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(50, cancellationToken);
            return Json(HttpStatusCode.OK, "{\"accessToken\":\"shared-token\",\"expiresIn\":600}");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://core.test") };
        using var provider = new CoreAccessTokenProvider(http, Options(), new MutableClock());

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => provider.GetAsync(default)));

        Assert.All(tokens, token => Assert.Equal("shared-token", token));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CoreClientRefreshesOnceOnUnauthorizedAndRetriesWithNewToken()
    {
        var tokenCalls = 0;
        var apiCalls = 0;
        var handler = new DelegateHttpHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/edge-token")
            {
                var next = Interlocked.Increment(ref tokenCalls);
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $$"""{"accessToken":"token-{{next}}","expiresIn":600}"""));
            }

            Interlocked.Increment(ref apiCalls);
            var bearer = request.Headers.Authorization?.Parameter;
            return Task.FromResult(bearer == "token-1"
                ? Json(HttpStatusCode.Unauthorized, "{}")
                : Json(HttpStatusCode.OK, "{\"status\":0,\"destinationCode\":\"00123\"}"));
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://core.test") };
        var options = Options();
        using var provider = new CoreAccessTokenProvider(http, options, new MutableClock());
        var client = new CoreApiClient(http, provider, options);

        var result = await client.LookupInboundAsync(InboundRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(CoreParcelDisposition.Normal, result.Disposition);
        Assert.Equal(2, tokenCalls);
        Assert.Equal(2, apiCalls);
    }

    [Fact]
    public async Task ProviderRejectsMalformedResponseWithoutEchoingCredential()
    {
        const string sensitiveSecret = "sensitive-device-credential";
        var handler = new DelegateHttpHandler((_, _, _) => Task.FromResult(Json(HttpStatusCode.OK, "{}")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://core.test") };
        var options = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            LogicalDeviceCode = "edge-01",
            DeviceSecret = sensitiveSecret
        });
        using var provider = new CoreAccessTokenProvider(http, options, new MutableClock());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(TestContext.Current.CancellationToken));

        Assert.DoesNotContain(sensitiveSecret, exception.Message, StringComparison.Ordinal);
        Assert.Equal("/api/auth/edge-token", handler.LastRequestPath);
    }

    [Fact]
    public async Task ProviderRejectsOversizedTokenResponseWithoutEchoingBodyOrCredential()
    {
        const string sensitiveSecret = "sensitive-device-credential";
        const string sensitiveBody = "sensitive-token-response";
        var handler = new DelegateHttpHandler((_, _, _) => Task.FromResult(Json(
            HttpStatusCode.OK,
            $$"""{"accessToken":"{{sensitiveBody}}"}""")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://core.test") };
        var options = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            LogicalDeviceCode = "edge-01",
            DeviceSecret = sensitiveSecret,
            MaximumResponseBytes = 8
        });
        using var provider = new CoreAccessTokenProvider(http, options, new MutableClock());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAsync(TestContext.Current.CancellationToken));

        Assert.Contains("size limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sensitiveSecret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveBody, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixedTokenIsUsableOnlyWhenExplicitlyEnabled()
    {
        using var http = new HttpClient(new DelegateHttpHandler((_, _, _) =>
            Task.FromResult(Json(HttpStatusCode.InternalServerError, "{}"))))
        {
            BaseAddress = new Uri("https://core.test")
        };
        var disabled = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            BearerToken = "fixed-but-disabled"
        });
        using var disabledProvider = new CoreAccessTokenProvider(http, disabled, new MutableClock());
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabledProvider.GetAsync(TestContext.Current.CancellationToken));

        var enabled = Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            UseFixedBearerToken = true,
            BearerToken = "fixed-enabled"
        });
        using var enabledProvider = new CoreAccessTokenProvider(http, enabled, new MutableClock());
        Assert.Equal("fixed-enabled", await enabledProvider.GetAsync(TestContext.Current.CancellationToken));
    }

    private static Microsoft.Extensions.Options.IOptions<CoreApiOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new CoreApiOptions
        {
            LogicalDeviceCode = "edge-01",
            DeviceSecret = "test-only-credential",
            TokenRefreshSkew = TimeSpan.FromMinutes(2)
        });

    private static CoreInboundLookupRequest InboundRequest() => new(
        Guid.NewGuid(),
        "11111111-1111-1111-1111-111111111111",
        ["123450000000005954400001"],
        "Parcel",
        "Express",
        "12345",
        "59544",
        100,
        10,
        20,
        30,
        DateTimeOffset.UtcNow,
        Guid.NewGuid().ToString("D"));

    private static HttpResponseMessage Json(HttpStatusCode status, string payload) => new(status)
    {
        Content = new StringContent(payload, Encoding.UTF8, "application/json")
    };

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);

        internal void Advance(TimeSpan amount) => UtcNow += amount;
    }

    private sealed class DelegateHttpHandler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _requestCount;

        internal int RequestCount => _requestCount;
        internal string? LastRequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _requestCount);
            LastRequestPath = request.RequestUri?.AbsolutePath;
            return send(request, attempt, cancellationToken);
        }
    }
}
