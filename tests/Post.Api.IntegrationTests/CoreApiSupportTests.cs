using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Post.ApiSupport;
using Post.Core.ApiSupport;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Infrastructure;
using Post.Core.Infrastructure.Messaging;

namespace Post.Api.IntegrationTests;

public sealed class CoreApiSupportTests
{
    private const string SigningKey =
        "synthetic-core-api-support-signing-key-with-more-than-thirty-two-bytes";

    [Fact]
    public void AccessScopeHonorsGlobalRolesAndParsesCenterClaims()
    {
        var global = Principal(
            new Claim("sub", "global-user"),
            new Claim("role", "GlobalAdministrator"));
        var center = Principal(
            new Claim("sub", "center-user"),
            new Claim("role", "CenterAdministrator"),
            new Claim("allowed_center", "59544"),
            new Claim("allowed_centers", "[\"11000\",\"59544\"]"));

        var globalScope = global.ToAccessScope();
        var centerScope = center.ToAccessScope();

        Assert.True(globalScope.IsGlobal);
        Assert.False(centerScope.IsGlobal);
        Assert.Equal(["59544", "11000"], centerScope.AllowedCenterCodes);
        Assert.Equal("center-user", center.Actor());
    }

    [Fact]
    public void AccessScopeFailsClosedForMalformedCenterClaimAndActorFallsBack()
    {
        var principal = Principal(new Claim("allowed_centers", "[invalid"));

        var scope = principal.ToAccessScope();

        Assert.False(scope.IsGlobal);
        Assert.Empty(scope.AllowedCenterCodes);
        Assert.Equal("anonymous", principal.Actor());
    }

    [Fact]
    public void AuthenticatedEdgeRequiresDeviceIdentityAndExactlyOneCenter()
    {
        var edgeId = Guid.NewGuid();
        var identity = Principal(
            new Claim("role", "Edge"),
            new Claim("device_id", edgeId.ToString("D")),
            new Claim("allowed_center", "C1"))
            .AuthenticatedEdge();

        Assert.Equal(edgeId, identity.EdgeId);
        Assert.Equal("C1", identity.ExchangeCenterCode);
        Assert.Throws<CoreAuthorizationException>(() => Principal(
            new Claim("role", "Edge"),
            new Claim("allowed_center", "C1")).AuthenticatedEdge());
        Assert.Throws<CoreAuthorizationException>(() => Principal(
            new Claim("role", "Edge"),
            new Claim("device_id", edgeId.ToString("D")),
            new Claim("allowed_centers", "[\"C1\",\"C2\"]")).AuthenticatedEdge());
    }

    [Fact]
    public void HttpHelpersReturnValidatedIdentifiers()
    {
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-fallback"
        };
        context.Response.Headers["X-Correlation-ID"] = "corr-123";
        context.Request.Headers["Idempotency-Key"] = "  operation-123  ";

        Assert.Equal("corr-123", context.CorrelationId());
        Assert.Equal("operation-123", context.Request.RequireIdempotencyKey());

        context.Response.Headers.Remove("X-Correlation-ID");
        context.Request.Headers.Remove("Idempotency-Key");
        Assert.Equal("trace-fallback", context.CorrelationId());
        Assert.Throws<ArgumentException>(() => context.Request.RequireIdempotencyKey());
    }

    [Fact]
    public async Task StatelessProfileStartsWithoutDatabaseAndReportsReady()
    {
        await using var app = BuildApplication(CoreServiceProfile.DataIngestion);
        app.UsePostApiDefaults();
        await app.InitializeCoreServiceAsync(cancellationToken: TestContext.Current.CancellationToken);
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var response = await app.GetTestClient().GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        await using var scope = app.Services.CreateAsyncScope();

        Assert.True(response.IsSuccessStatusCode);
        Assert.NotNull(scope.ServiceProvider.GetService<IInboundValidationService>());
    }

    [Fact]
    public async Task StatefulProfileCreatesItsOwnedTestingDatabaseAndReportsReady()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), "post-core-api-tests", Guid.NewGuid().ToString("N"));
        WebApplication? app = null;
        try
        {
            app = BuildApplication(CoreServiceProfile.ParcelLifecycle, databaseDirectory);
            app.UsePostApiDefaults();
            await app.InitializeCoreServiceAsync(cancellationToken: TestContext.Current.CancellationToken);
            await app.StartAsync(TestContext.Current.CancellationToken);

            using var response = await app.GetTestClient().GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

            Assert.True(response.IsSuccessStatusCode);
            Assert.True(File.Exists(Path.Combine(databaseDirectory, "parcel-lifecycle.db")));
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync(TestContext.Current.CancellationToken);
                await app.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(databaseDirectory))
            {
                Directory.Delete(databaseDirectory, true);
            }
        }
    }

    [Fact]
    public async Task AllProfileChecksEveryOwnedDatabaseBeforeReportingReady()
    {
        var databaseDirectory = Path.Combine(Path.GetTempPath(), "post-core-all-health", Guid.NewGuid().ToString("N"));
        WebApplication? app = null;
        try
        {
            app = BuildApplication(CoreServiceProfile.All, databaseDirectory);
            app.UsePostApiDefaults();
            await app.InitializeCoreServiceAsync(cancellationToken: TestContext.Current.CancellationToken);
            await app.StartAsync(TestContext.Current.CancellationToken);

            using var response = await app.GetTestClient().GetAsync(
                new Uri("/health/ready", UriKind.Relative),
                TestContext.Current.CancellationToken);

            Assert.True(response.IsSuccessStatusCode);
            Assert.Equal(5, Directory.GetFiles(databaseDirectory, "*.db").Length);
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync(TestContext.Current.CancellationToken);
                await app.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            if (Directory.Exists(databaseDirectory))
            {
                Directory.Delete(databaseDirectory, true);
            }
        }
    }

    [Fact]
    public void ProductionRejectsSqliteEvenWhenAnOwnedConnectionStringExists()
    {
        var builder = ProductionBuilder(new Dictionary<string, string?>
        {
            ["Core:Database:Provider"] = "Sqlite",
            ["ConnectionStrings:ParcelLifecycle"] =
                "Server=(localdb)\\MSSQLLocalDB;Database=PostCoreTest;Integrated Security=true"
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddCoreServiceDefaults("Post.Test.Core", CoreServiceProfile.ParcelLifecycle));

        Assert.Contains("Provider=SqlServer", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Development or Testing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionStatefulProfileRequiresItsOwnedConnectionString()
    {
        var builder = ProductionBuilder(new Dictionary<string, string?>
        {
            ["Core:Database:Provider"] = "SqlServer",
            ["ConnectionStrings:ParcelLifecycle"] = " ",
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] =
                string.Concat(
                    "redis.example.invalid:6380,ssl=true,abortConnect=false,user=post-core,",
                    "pass",
                    "word=",
                    "synthetic-redis-",
                    "test-value")
        });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddCoreServiceDefaults("Post.Test.Core", CoreServiceProfile.ParcelLifecycle));

        Assert.Contains("ParcelLifecycle", exception.Message, StringComparison.Ordinal);
        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Sqlite")]
    public async Task DevelopmentDefaultsToIsolatedSqliteUnlessSqlServerIsExplicit(string? provider)
    {
        var databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "post-core-development-profile-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var builder = EnvironmentBuilder("Development", new Dictionary<string, string?>
            {
                ["Core:Database:Provider"] = provider,
                ["Core:Database:Directory"] = databaseDirectory
            });
            builder.AddCoreServiceDefaults("Post.Test.Core", CoreServiceProfile.DataIngestion);
            await using var app = builder.Build();

            var options = app.Services.GetRequiredService<CoreInfrastructureOptions>();
            var publisher = app.Services.GetRequiredService<IDurableEventPublisher>();

            Assert.Equal(CoreInfrastructureEnvironment.Testing, options.Environment);
            Assert.Equal(CoreDatabaseProvider.Sqlite, options.DatabaseProvider);
            Assert.IsType<InMemoryDurableEventPublisher>(publisher);
        }
        finally
        {
            if (Directory.Exists(databaseDirectory))
            {
                Directory.Delete(databaseDirectory, true);
            }
        }
    }

    [Fact]
    public async Task DevelopmentExplicitSqlServerUsesRealInfrastructureAdapters()
    {
        var builder = EnvironmentBuilder("Development", new Dictionary<string, string?>
        {
            ["Core:Database:Provider"] = "SqlServer",
            ["Messaging:Kafka:BootstrapServers"] = "redpanda:9092"
        });
        builder.AddCoreServiceDefaults("Post.Test.Core", CoreServiceProfile.DataIngestion);
        await using var app = builder.Build();

        var options = app.Services.GetRequiredService<CoreInfrastructureOptions>();
        var publisher = app.Services.GetRequiredService<IDurableEventPublisher>();

        Assert.Equal(CoreInfrastructureEnvironment.Development, options.Environment);
        Assert.Equal(CoreDatabaseProvider.SqlServer, options.DatabaseProvider);
        Assert.IsType<KafkaDurableEventPublisher>(publisher);
    }

    [Fact]
    public async Task TestingAlwaysUsesSqliteEvenWhenSqlServerIsRequested()
    {
        var databaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "post-core-testing-profile-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var builder = EnvironmentBuilder("Testing", new Dictionary<string, string?>
            {
                ["Core:Database:Provider"] = "SqlServer",
                ["Core:Database:Directory"] = databaseDirectory
            });
            builder.AddCoreServiceDefaults("Post.Test.Core", CoreServiceProfile.DataIngestion);
            await using var app = builder.Build();

            var options = app.Services.GetRequiredService<CoreInfrastructureOptions>();

            Assert.Equal(CoreInfrastructureEnvironment.Testing, options.Environment);
            Assert.Equal(CoreDatabaseProvider.Sqlite, options.DatabaseProvider);
        }
        finally
        {
            if (Directory.Exists(databaseDirectory))
            {
                Directory.Delete(databaseDirectory, true);
            }
        }
    }

    private static WebApplication BuildApplication(CoreServiceProfile profile, string? databaseDirectory = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = "false",
            ["Api:MetricsEnabled"] = "false",
            ["Auth:Issuer"] = "post-core-api-support-tests",
            ["Auth:Audience"] = "post-platform-tests",
            ["Auth:SigningKey"] = SigningKey,
            ["Core:Outbox:Enabled"] = "false",
            ["Core:Database:ApplyMigrations"] = "false",
            ["Core:Database:Directory"] = databaseDirectory
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddCoreServiceDefaults("Post.Test.Core", profile);
        return builder.Build();
    }

    private static WebApplicationBuilder ProductionBuilder(
        IReadOnlyDictionary<string, string?> additionalSettings)
        => EnvironmentBuilder("Production", additionalSettings);

    private static WebApplicationBuilder EnvironmentBuilder(
        string environmentName,
        IReadOnlyDictionary<string, string?> additionalSettings)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Api:AuthenticationEnabled"] = environmentName == "Production" ? "true" : "false",
            ["Api:MetricsEnabled"] = "false",
            ["Auth:Issuer"] = "post-core-api-support-tests",
            ["Auth:Audience"] = "post-platform-tests",
            ["Auth:SigningKey"] = SigningKey,
            ["Core:Outbox:Enabled"] = "false"
        };
        foreach (var pair in additionalSettings)
        {
            settings[pair.Key] = pair.Value;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environmentName
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test", "sub", "role"));
}
