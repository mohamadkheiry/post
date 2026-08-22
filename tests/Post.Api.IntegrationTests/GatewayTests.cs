using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Post.Core.Gateway;
using Yarp.ReverseProxy.Configuration;

namespace Post.Api.IntegrationTests;

public sealed class GatewayTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;

    public GatewayTests(GatewayFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void ForwardedHeaderConfigurationRequiresExplicitValidTrustBoundary()
    {
        var configured = GatewayForwardedHeadersFactory.Create(new Post.Core.Gateway.GatewaySecurityOptions
        {
            ForwardedHeadersEnabled = true,
            ForwardedHeadersForwardLimit = 2,
            ForwardedHeadersRequireHeaderSymmetry = true,
            ForwardedHeadersKnownProxies = ["10.0.0.10"],
            ForwardedHeadersKnownNetworks = ["192.0.2.0/24"]
        });

        Assert.Equal(2, configured.ForwardLimit);
        Assert.True(configured.RequireHeaderSymmetry);
        Assert.Single(configured.KnownProxies);
        Assert.Single(configured.KnownIPNetworks);
        Assert.Throws<InvalidOperationException>(() => GatewayForwardedHeadersFactory.Create(
            new Post.Core.Gateway.GatewaySecurityOptions { ForwardedHeadersEnabled = true }));
        Assert.Throws<InvalidOperationException>(() => GatewayForwardedHeadersFactory.Create(
            new Post.Core.Gateway.GatewaySecurityOptions
            {
                ForwardedHeadersEnabled = true,
                ForwardedHeadersKnownNetworks = ["not-a-cidr"]
            }));
    }

    [Fact]
    public async Task HealthEndpointReturnsSecurityAndCorrelationHeaders()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Correlation-ID", "unsafe header value");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(Guid.TryParse(Assert.Single(response.Headers.GetValues("X-Correlation-ID")), out _));
    }

    [Fact]
    public async Task RootDescribesTheGatewayWithoutAuthenticationInTesting()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        using var metrics = await client.GetAsync(new Uri("/metrics", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(metrics.IsSuccessStatusCode);
        Assert.Contains("Post.Core.Gateway", body, StringComparison.Ordinal);
        Assert.Contains("ready", body, StringComparison.Ordinal);
        Assert.Contains(
            _factory.Services.GetServices<IExceptionHandler>(),
            handler => handler is GatewayExceptionHandler);
    }

    [Fact]
    public async Task GatewayRejectsOversizedJsonBeforeProxyingForLengthAndChunkedBodies()
    {
        using var client = _factory.CreateClient();
        var oversized = Encoding.UTF8.GetBytes(string.Concat(
            "{\"padding\":\"",
            new string('x', 2 * 1024 * 1024),
            "\"}"));

        using var knownLength = JsonRequest(oversized, chunked: false);
        using var knownLengthResponse = await client.SendAsync(knownLength, TestContext.Current.CancellationToken);
        using var chunked = JsonRequest(oversized, chunked: true);
        using var chunkedResponse = await client.SendAsync(chunked, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, knownLengthResponse.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
    }

    [Fact]
    public void IdentityBootstrapAndCredentialLifecycleRoutesAreMappedToIdentity()
    {
        var provider = _factory.Services.GetRequiredService<IProxyConfigProvider>();
        var routes = provider.GetConfig().Routes.ToDictionary(x => x.RouteId, StringComparer.Ordinal);

        Assert.Equal("Anonymous", routes["identity-auth"].AuthorizationPolicy);
        Assert.Equal("identity", routes["identity-admin-edges"].ClusterId);
        Assert.Equal("identity", routes["identity-admin-edge-rotate"].ClusterId);
        Assert.Equal("identity", routes["identity-admin-edge-disable"].ClusterId);
        Assert.Equal(["POST"], routes["identity-admin-edges"].Match.Methods);
        Assert.Equal(["POST"], routes["identity-admin-edge-rotate"].Match.Methods);
        Assert.Equal(["POST"], routes["identity-admin-edge-disable"].Match.Methods);
    }

    private static HttpRequestMessage JsonRequest(byte[] payload, bool chunked)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/auth/token", UriKind.Relative))
        {
            Content = new StreamContent(new MemoryStream(payload, writable: false))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (chunked)
        {
            request.Content.Headers.ContentLength = null;
            request.Headers.TransferEncodingChunked = true;
        }

        return request;
    }
}

public sealed class GatewayExceptionHandlerTests
{
    [Fact]
    public async Task UnexpectedFailurePreservesCorrelationWithoutLeakingExceptionData()
    {
        const string sentinel =
            "gateway-sensitive-marker https://user:credential@private.example/query?token=raw-secret";
        const string innerSentinel = "gateway-inner-sensitive-response-body";
        var correlationId = Guid.NewGuid().ToString("D");
        var messages = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new CoreApiLogProvider(messages));
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GatewayExceptionHandler>();

        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.Use(async (context, next) =>
        {
            context.TraceIdentifier = correlationId;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
                return Task.CompletedTask;
            });
            await next(context).ConfigureAwait(false);
        });
        app.MapGet(
            "/fault",
            () => Task.FromException<IResult>(new InvalidOperationException(
                sentinel,
                new HttpRequestException(innerSentinel))));

        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = app.GetTestClient();
            using var response = await client.GetAsync(new Uri("/fault", UriKind.Relative), TestContext.Current.CancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using var problem = JsonDocument.Parse(responseBody);
            var combinedLogs = string.Join(Environment.NewLine, messages);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(
                correlationId,
                Assert.Single(response.Headers.GetValues("X-Correlation-ID")));
            Assert.Equal("gateway.error", problem.RootElement.GetProperty("code").GetString());
            Assert.Equal(correlationId, problem.RootElement.GetProperty("traceId").GetString());
            Assert.Contains(nameof(InvalidOperationException), combinedLogs, StringComparison.Ordinal);
            Assert.Contains(correlationId, combinedLogs, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, combinedLogs, StringComparison.Ordinal);
            Assert.DoesNotContain(innerSentinel, responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain(innerSentinel, combinedLogs, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-secret", responseBody, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-secret", combinedLogs, StringComparison.Ordinal);
            Assert.Contains(
                app.Services.GetServices<IExceptionHandler>(),
                handler => handler is GatewayExceptionHandler);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}

public sealed class GatewayFactory : WebApplicationFactory<Post.Core.Gateway.GatewaySecurityOptions>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Auth:AuthenticationEnabled"] = "false"
            }));
    }
}

public sealed class AuthenticatedGatewayTests : IClassFixture<AuthenticatedGatewayFactory>
{
    private const string SyntheticPassphrase = "not-a-real-password";
    internal const string SigningKey =
        "synthetic-gateway-signing-key-with-more-than-sixty-four-bytes-for-algorithm-tests";
    private readonly AuthenticatedGatewayFactory _factory;

    public AuthenticatedGatewayTests(AuthenticatedGatewayFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BootstrapAuthenticationRouteIsAnonymousWhileBusinessRoutesRequireBearerToken()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var tokenResponse = await client.PostAsJsonAsync("/api/auth/token", new
        {
            userName = "nobody",
            password = SyntheticPassphrase
        }, cancellationToken: TestContext.Current.CancellationToken);
        using var protectedResponse = await client.GetAsync(new Uri("/api/admin/parcels", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, tokenResponse.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
    }

    [Fact]
    public async Task GatewayAcceptsHs256AndRejectsHs512()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var acceptedRequest = ProtectedRequest(SecurityAlgorithms.HmacSha256);
        using var accepted = await client.SendAsync(acceptedRequest, TestContext.Current.CancellationToken);
        using var rejectedRequest = ProtectedRequest(SecurityAlgorithms.HmacSha512);
        using var rejected = await client.SendAsync(rejectedRequest, TestContext.Current.CancellationToken);

        Assert.NotEqual(System.Net.HttpStatusCode.Unauthorized, accepted.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedEdgeAddsSafeIdentityFieldsToGatewayLogScope()
    {
        var edgeId = Guid.NewGuid();
        using var client = _factory.CreateClient();
        using var request = EdgeScopeRequest(edgeId, "CENTER-02");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains(_factory.LogMessages, message =>
            message.Contains("Post.Core.Gateway.AuthenticatedRequest scope", StringComparison.Ordinal) &&
            message.Contains($"EdgeId={edgeId:D}", StringComparison.Ordinal) &&
            message.Contains("ExchangeCenterCode=CENTER-02", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.LogMessages, message =>
            message.Contains("Bearer ", StringComparison.Ordinal) ||
            message.Contains(SigningKey, StringComparison.Ordinal));
    }

    private static HttpRequestMessage ProtectedRequest(string algorithm)
    {
        var now = DateTime.UtcNow;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            algorithm);
        var token = new JwtSecurityToken(
            "post-core-tests",
            "post-core-tests",
            [new Claim(JwtRegisteredClaimNames.Sub, "gateway-test-subject")],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            credentials);
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("/api/admin/parcels", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    private static HttpRequestMessage EdgeScopeRequest(Guid edgeId, string center)
    {
        var now = DateTime.UtcNow;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            "post-core-tests",
            "post-core-tests",
            [
                new Claim(JwtRegisteredClaimNames.Sub, edgeId.ToString("D")),
                new Claim("device_id", edgeId.ToString("D")),
                new Claim("allowed_center", center),
                new Claim("role", "Edge")
            ],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            credentials);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }
}

public sealed class AuthenticatedGatewayFactory : WebApplicationFactory<Post.Core.Gateway.GatewaySecurityOptions>
{
    private readonly ConcurrentQueue<string> _logMessages = new();

    public IReadOnlyCollection<string> LogMessages => _logMessages.ToArray();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("AuthTesting");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Auth:SigningKey"] = AuthenticatedGatewayTests.SigningKey
            }));
        builder.ConfigureServices(services =>
            services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(
                _ => new CoreApiLogProvider(_logMessages)));
    }
}

public sealed class RateLimitedGatewayTests : IClassFixture<RateLimitedGatewayFactory>
{
    private readonly RateLimitedGatewayFactory _factory;

    public RateLimitedGatewayTests(RateLimitedGatewayFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RequestsBeyondConfiguredPartitionLimitAreRejected()
    {
        using var client = _factory.CreateClient();

        using var accepted = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        using var rejected = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.True(accepted.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }
}

public sealed class RateLimitedGatewayFactory : WebApplicationFactory<Post.Core.Gateway.GatewaySecurityOptions>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Auth:AuthenticationEnabled"] = "false",
                ["Auth:RateLimitPermitLimit"] = "1",
                ["Auth:RateLimitTokensPerPeriod"] = "1",
                ["Auth:RateLimitReplenishmentSeconds"] = "3600",
                ["Auth:RateLimitQueueLimit"] = "0"
            }));
    }
}

public sealed class TrustedForwardedGatewayTests : IClassFixture<TrustedForwardedGatewayFactory>
{
    private readonly TrustedForwardedGatewayFactory _factory;

    public TrustedForwardedGatewayTests(TrustedForwardedGatewayFactory factory) => _factory = factory;

    [Fact]
    public async Task TrustedProxyForwardedClientsReceiveIndependentRatePartitions()
    {
        using var client = _factory.CreateClient();
        using var first = await client.SendAsync(ForwardedRequest("203.0.113.10"), TestContext.Current.CancellationToken);
        using var second = await client.SendAsync(ForwardedRequest("203.0.113.11"), TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccessStatusCode);
        Assert.True(second.IsSuccessStatusCode);
    }

    internal static HttpRequestMessage ForwardedRequest(string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health", UriKind.Relative));
        request.Headers.Add("X-Forwarded-For", address);
        request.Headers.Add("X-Forwarded-Proto", "http");
        return request;
    }
}

public sealed class UntrustedForwardedGatewayTests : IClassFixture<UntrustedForwardedGatewayFactory>
{
    private readonly UntrustedForwardedGatewayFactory _factory;

    public UntrustedForwardedGatewayTests(UntrustedForwardedGatewayFactory factory) => _factory = factory;

    [Fact]
    public async Task UntrustedProxyCannotSpoofRateLimitPartitionWithForwardingHeader()
    {
        using var client = _factory.CreateClient();
        using var first = await client.SendAsync(TrustedForwardedGatewayTests.ForwardedRequest("203.0.113.10"), TestContext.Current.CancellationToken);
        using var second = await client.SendAsync(TrustedForwardedGatewayTests.ForwardedRequest("203.0.113.11"), TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, second.StatusCode);
    }
}

public sealed class TrustedForwardedGatewayFactory : ForwardedGatewayFactory
{
    protected override string RemoteAddress => "10.0.0.1";
}

public sealed class UntrustedForwardedGatewayFactory : ForwardedGatewayFactory
{
    protected override string RemoteAddress => "10.0.0.2";
}

public abstract class ForwardedGatewayFactory : WebApplicationFactory<Post.Core.Gateway.GatewaySecurityOptions>
{
    protected abstract string RemoteAddress { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Auth:AuthenticationEnabled"] = "false",
                ["Auth:RateLimitPermitLimit"] = "1",
                ["Auth:RateLimitTokensPerPeriod"] = "1",
                ["Auth:RateLimitReplenishmentSeconds"] = "3600",
                ["Auth:RateLimitQueueLimit"] = "0",
                ["Auth:ForwardedHeadersEnabled"] = "true",
                ["Auth:ForwardedHeadersForwardLimit"] = "1",
                ["Auth:ForwardedHeadersRequireHeaderSymmetry"] = "true",
                ["Auth:ForwardedHeadersKnownProxies:0"] = "10.0.0.1"
            }));
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new FixedRemoteAddressStartupFilter(RemoteAddress)));
    }

    private sealed class FixedRemoteAddressStartupFilter(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(address);
                await continuation().ConfigureAwait(false);
            });
            next(application);
        };
    }
}
