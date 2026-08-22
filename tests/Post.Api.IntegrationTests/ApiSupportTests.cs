using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Post.ApiSupport;
using Post.Core.Application.Common;

namespace Post.Api.IntegrationTests;

public sealed class ApiSupportTests
{
    private const string JwtIssuer = "post-api-support-tests";
    private const string JwtAudience = "post-platform-tests";
    private const string JwtSigningKey =
        "synthetic-api-support-jwt-signing-key-with-more-than-sixty-four-bytes";

    [Fact]
    public async Task DefaultsExposeHealthRootCorrelationAndSecurityHeaders()
    {
        await using var app = BuildApplication();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        var root = await client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);
        var health = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        var metrics = await client.GetAsync(new Uri("/metrics", UriKind.Relative), TestContext.Current.CancellationToken);
        var metricsBody = await metrics.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.True(root.IsSuccessStatusCode);
        Assert.True(health.IsSuccessStatusCode);
        Assert.True(
            metrics.IsSuccessStatusCode,
            $"Metrics returned {(int)metrics.StatusCode} ({metrics.StatusCode}): {metricsBody}");
        Assert.True(root.Headers.Contains("X-Correlation-ID"));
        Assert.Equal("nosniff", Assert.Single(root.Headers.GetValues("X-Content-Type-Options")));
        Assert.Contains("Post.Test.Api", await root.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExceptionHandlerMapsSafeValidationErrorToProblemDetails()
    {
        await using var app = BuildApplication(builder =>
            builder.MapGet("/fail", ThrowValidationError));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync(new Uri("/fail", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("validation.failed", body, StringComparison.Ordinal);
        Assert.Contains("A safe validation message.", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExceptionHandlerRedactsDependencyDetails()
    {
        var logMessages = new ConcurrentQueue<string>();
        await using var app = BuildApplication(
            application => application.MapGet("/dependency-failure", ThrowDependencyError),
            builder => builder.Services.AddSingleton<ILoggerProvider>(
                _ => new CoreApiLogProvider(logMessages)));
        await app.StartAsync(TestContext.Current.CancellationToken);

        var response = await app.GetTestClient().GetAsync(new Uri("/dependency-failure", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var traceId = Assert.Single(response.Headers.GetValues("X-Correlation-ID"));

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("dependency.unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-upstream-host", body, StringComparison.Ordinal);
        Assert.DoesNotContain(logMessages, message =>
            message.Contains("private-upstream-host", StringComparison.Ordinal));
        Assert.Contains(logMessages, message =>
            message.Contains(nameof(CoreDependencyUnavailableException), StringComparison.Ordinal) &&
            message.Contains(traceId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitDevelopmentAuthenticationBypassAlsoBypassesNamedPolicies()
    {
        await using var app = BuildApplication(builder =>
            builder.MapGet("/edge-policy", () => Results.Ok())
                .RequireAuthorization(PostAuthorizationPolicies.Edge));
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await app.GetTestClient().GetAsync(new Uri("/edge-policy", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public void DefaultsRejectDisabledAuthenticationOutsideSafeEnvironments()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "false"
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production"
        });
        builder.Configuration.AddInMemoryCollection(settings);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPostApiDefaults("Post.Test.Api"));

        Assert.Contains("Development or Testing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsRequireStrongSigningKeyWhenAuthenticationIsEnabled()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "true",
            ["Auth:SigningKey"] = "short"
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(settings);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPostApiDefaults("Post.Test.Api"));

        Assert.Contains("32 UTF-8 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultsConfigureUtcJsonConsoleWithScopes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "false"
        });

        builder.AddPostApiDefaults("Post.Test.Api");
        using var provider = builder.Services.BuildServiceProvider();
        var console = provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;
        var json = provider.GetRequiredService<IOptions<JsonConsoleFormatterOptions>>().Value;

        Assert.Equal(ConsoleFormatterNames.Json, console.FormatterName);
        Assert.True(json.IncludeScopes);
        Assert.True(json.UseUtcTimestamp);
        Assert.Equal("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", json.TimestampFormat);
    }

    [Fact]
    public async Task DefaultsRejectOversizedJsonBeforeEndpointBindingForLengthAndChunkedBodies()
    {
        var calls = 0;
        await using var app = BuildApplication(application =>
            application.MapPost("/bounded-json", (JsonElement _) =>
            {
                Interlocked.Increment(ref calls);
                return Results.Ok();
            }));
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();
        var oversized = Encoding.UTF8.GetBytes(string.Concat(
            "{\"padding\":\"",
            new string('x', 2 * 1024 * 1024),
            "\"}"));

        using var knownLength = JsonStreamRequest("/bounded-json", oversized, chunked: false);
        using var knownLengthResponse = await client.SendAsync(knownLength, TestContext.Current.CancellationToken);
        using var chunked = JsonStreamRequest("/bounded-json", oversized, chunked: true);
        using var chunkedResponse = await client.SendAsync(chunked, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, knownLengthResponse.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(8191)]
    [InlineData(16777217)]
    public void DefaultsRejectUnsafeJsonBodyLimit(long bodyLimit)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "false",
            ["Api:JsonRequestBodyMaximumBytes"] = bodyLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPostApiDefaults("Post.Test.Api"));

        Assert.Contains("JsonRequestBodyMaximumBytes", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", JwtAudience)]
    [InlineData(JwtIssuer, " ")]
    public void DefaultsRequireNonblankIssuerAndAudience(string issuer, string audience)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "true",
            ["Auth:Issuer"] = issuer,
            ["Auth:Audience"] = audience,
            ["Auth:SigningKey"] = JwtSigningKey
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.Configuration.AddInMemoryCollection(settings);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddPostApiDefaults("Post.Test.Api"));

        Assert.Contains("Issuer", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Audience", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JwtValidationAcceptsHs256AndRejectsHs512()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "true",
            ["Api:SwaggerEnabled"] = "false",
            ["Api:MetricsEnabled"] = "false",
            ["Auth:Issuer"] = JwtIssuer,
            ["Auth:Audience"] = JwtAudience,
            ["Auth:SigningKey"] = JwtSigningKey,
            ["Auth:RequireHttpsMetadata"] = "false"
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPostApiDefaults("Post.Test.Api");
        await using var app = builder.Build();
        app.UsePostApiDefaults();
        app.MapGet("/edge-policy", () => Results.Ok())
            .RequireAuthorization(PostAuthorizationPolicies.Edge);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();

        using var acceptedRequest = BearerRequest(SecurityAlgorithms.HmacSha256);
        using var accepted = await client.SendAsync(acceptedRequest, TestContext.Current.CancellationToken);
        using var rejectedRequest = BearerRequest(SecurityAlgorithms.HmacSha512);
        using var rejected = await client.SendAsync(rejectedRequest, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedEdgeRequestAddsOnlySafeIdentityValuesToStructuredLogScope()
    {
        var messages = new ConcurrentQueue<string>();
        var edgeId = Guid.NewGuid();
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "true",
            ["Api:SwaggerEnabled"] = "false",
            ["Api:MetricsEnabled"] = "false",
            ["Auth:Issuer"] = JwtIssuer,
            ["Auth:Audience"] = JwtAudience,
            ["Auth:SigningKey"] = JwtSigningKey,
            ["Auth:RequireHttpsMetadata"] = "false"
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPostApiDefaults("Post.Test.Api");
        builder.Services.AddSingleton<ILoggerProvider>(_ => new CoreApiLogProvider(messages));
        await using var app = builder.Build();
        app.UsePostApiDefaults();
        app.MapGet("/edge-scope", () => Results.Ok())
            .RequireAuthorization(PostAuthorizationPolicies.Edge);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var request = EdgeBearerRequest(edgeId, "CENTER-01");
        using var response = await app.GetTestClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(messages, message =>
            message.Contains("Post.AuthenticatedRequest scope", StringComparison.Ordinal) &&
            message.Contains($"EdgeId={edgeId:D}", StringComparison.Ordinal) &&
            message.Contains("ExchangeCenterCode=CENTER-01", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message =>
            message.Contains("Bearer ", StringComparison.Ordinal) ||
            message.Contains(JwtSigningKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProductionHealthResponseRedactsCheckNamesAndDescriptions()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "true",
            ["Api:SwaggerEnabled"] = "false",
            ["Api:MetricsEnabled"] = "false",
            ["Auth:Issuer"] = JwtIssuer,
            ["Auth:Audience"] = JwtAudience,
            ["Auth:SigningKey"] = JwtSigningKey
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPostApiDefaults("Post.Test.Api");
        builder.Services.AddHealthChecks().AddCheck(
            "private-database-host",
            () => HealthCheckResult.Unhealthy("private-sql-error-detail"));
        await using var app = builder.Build();
        app.UsePostApiDefaults();
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await app.GetTestClient().GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("Unhealthy", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-database-host", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sql-error-detail", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnabledDevelopmentIdentityFailsClosedWhenIdentityConfigurationIsInvalid()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "false",
            ["Api:SwaggerEnabled"] = "false",
            ["Api:DevelopmentIdentity:Enabled"] = "true",
            ["Api:DevelopmentIdentity:EdgeId"] = Guid.Empty.ToString("D"),
            ["Api:DevelopmentIdentity:ExchangeCenterCode"] = "C1"
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPostApiDefaults("Post.Test.Api");
        await using var app = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => app.UsePostApiDefaults());

        Assert.Contains("development identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static WebApplication BuildApplication(
        Action<WebApplication>? map = null,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "false",
            ["Api:SwaggerEnabled"] = "false",
            // Project-reference appsettings files are copied to the test output folder.
            // Pin this feature on so the shared-host test cannot inherit a service's
            // Testing override and still exercises the real metrics endpoint.
            ["Api:MetricsEnabled"] = "true"
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddPostApiDefaults("Post.Test.Api");
        configureBuilder?.Invoke(builder);
        var app = builder.Build();
        app.UsePostApiDefaults();
        map?.Invoke(app);
        return app;
    }

    private static IResult ThrowValidationError() =>
        throw new ArgumentException("A safe validation message.");

    private static IResult ThrowDependencyError() =>
        throw new CoreDependencyUnavailableException("private-upstream-host must remain redacted");

    private static HttpRequestMessage BearerRequest(string algorithm)
    {
        var now = DateTime.UtcNow;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
            algorithm);
        var token = new JwtSecurityToken(
            JwtIssuer,
            JwtAudience,
            [new Claim(JwtRegisteredClaimNames.Sub, "edge"), new Claim("role", "Edge")],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            credentials);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/edge-policy", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    private static HttpRequestMessage EdgeBearerRequest(Guid edgeId, string center)
    {
        var now = DateTime.UtcNow;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            JwtIssuer,
            JwtAudience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, edgeId.ToString("D")),
                new Claim("device_id", edgeId.ToString("D")),
                new Claim("allowed_center", center),
                new Claim("role", "Edge")
            ],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            credentials);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/edge-scope", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    private static HttpRequestMessage JsonStreamRequest(string path, byte[] payload, bool chunked)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
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
