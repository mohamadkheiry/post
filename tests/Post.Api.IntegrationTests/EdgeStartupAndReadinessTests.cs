using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Post.Edge.Api;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Workers;
using EdgeConfigurationSource = Post.Edge.Application.Ports.IConfigurationSource;

namespace Post.Api.IntegrationTests;

public sealed class EdgeStartupAndReadinessTests
{
    private static readonly string[] LoopbackIpAddresses = ["127.0.0.1"];
    private static readonly string SyntheticPassword = string.Concat("synthetic-", "password");

    [Fact]
    public async Task StartupContinuesWithCachedConfigurationWhenSourceIsUnavailable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"post-edge-cache-{Guid.NewGuid():N}.db");
        await using (var warm = new EdgeApiFactory(databasePath: databasePath, deleteDatabaseOnDispose: false))
        {
            using var client = warm.CreateClient();
            using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        await using var offline = new EdgeApiFactory(
            configureServices: services =>
            {
                services.RemoveAll<EdgeConfigurationSource>();
                services.AddSingleton<EdgeConfigurationSource, UnavailableConfigurationSource>();
            },
            databasePath: databasePath);
        using var offlineClient = offline.CreateClient();
        using var offlineReady = await offlineClient.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, offlineReady.StatusCode);
    }

    [Fact]
    public void StartupFailsClosedWhenSourceAndCachedConfigurationAreUnavailable()
    {
        using var factory = new EdgeApiFactory(configureServices: services =>
        {
            services.RemoveAll<EdgeConfigurationSource>();
            services.AddSingleton<EdgeConfigurationSource, UnavailableConfigurationSource>();
        });

        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("no cached snapshot", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NewerConfigurationReplacesCachedSnapshotAtomically()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"post-edge-config-{Guid.NewGuid():N}.db");
        await using (var first = new EdgeApiFactory(databasePath: databasePath, deleteDatabaseOnDispose: false))
        {
            using var client = first.CreateClient();
            using var status = await client.GetAsync(new Uri("/api/edge/status", UriKind.Relative), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        await using var second = new EdgeApiFactory(
            new Dictionary<string, string?> { ["Edge:Mock:ConfigurationVersion"] = "2" },
            databasePath: databasePath);
        using var secondClient = second.CreateClient();
        var json = await secondClient.GetStringAsync(new Uri("/api/edge/status", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Contains("\"configurationVersion\":2", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndpointValidationReturnsProblemDetailsWithCorrelationAndNoOrphanAudit()
    {
        await using var factory = new EdgeApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri("/admin/devices/unknown/ip", UriKind.Relative))
        {
            Content = JsonContent.Create(new { ipAddresses = LoopbackIpAddresses })
        };
        request.Headers.Add("X-Correlation-ID", "edge-validation-correlation");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("edge-validation-correlation", response.Headers.GetValues("X-Correlation-ID").Single());
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TlsPolicyRejectsPlainHttpForDeviceAndAdministrationButAllowsHttps()
    {
        await using var factory = new EdgeApiFactory(new Dictionary<string, string?>
        {
            ["Edge:TransportTls:RequireTls"] = "true"
        });
        using var client = factory.CreateClient();

        using var deviceResponse = await client.GetAsync(new Uri("/ws/device", UriKind.Relative), TestContext.Current.CancellationToken);
        using var statusResponse = await client.GetAsync(new Uri("/api/edge/status", UriKind.Relative), TestContext.Current.CancellationToken);
        using var loginResponse = await client.PostAsJsonAsync(new Uri("/admin/auth/login", UriKind.Relative), new { userName = "admin", password = SyntheticPassword }, cancellationToken: TestContext.Current.CancellationToken);
        var body = await deviceResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var secureClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var secureStatus = await secureClient.GetAsync(new Uri("/api/edge/status", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UpgradeRequired, deviceResponse.StatusCode);
        Assert.Equal(HttpStatusCode.UpgradeRequired, statusResponse.StatusCode);
        Assert.Equal(HttpStatusCode.UpgradeRequired, loginResponse.StatusCode);
        Assert.Contains("TLS is required", body, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, secureStatus.StatusCode);
    }

    [Fact]
    public void ProductionTlsValidationRejectsMissingAndInvalidCertificates()
    {
        var environment = new StubHostEnvironment("Production", Path.GetTempPath());
        var missing = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var invalidPath = Path.Combine(Path.GetTempPath(), $"edge-invalid-{Guid.NewGuid():N}.pfx");
        File.WriteAllText(invalidPath, "not-a-certificate");
        try
        {
            var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Certificates:Default:Path"] = invalidPath
            }).Build();

            Assert.Throws<InvalidOperationException>(() => EdgeTlsConfiguration.Validate(missing, environment));
            Assert.Throws<InvalidOperationException>(() => EdgeTlsConfiguration.Validate(invalid, environment));
        }
        finally
        {
            File.Delete(invalidPath);
        }
    }

    [Fact]
    public void ProductionTlsValidationAcceptsCurrentPrivateKeyCertificate()
    {
        var certificatePassword = string.Concat("testing-certificate-", "password");
        var certificatePath = Path.Combine(Path.GetTempPath(), $"edge-valid-{Guid.NewGuid():N}.pfx");
        using var key = RSA.Create(2_048);
        var request = new CertificateRequest(
            "CN=edge-test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllBytes(certificatePath, certificate.Export(X509ContentType.Pfx, certificatePassword));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Certificates:Default:Path"] = certificatePath,
                ["Kestrel:Certificates:Default:Password"] = certificatePassword
            }).Build();

            var policy = EdgeTlsConfiguration.Validate(
                configuration,
                new StubHostEnvironment("Production", Path.GetTempPath()),
                DateTimeOffset.UtcNow);

            Assert.True(policy.RequiresTls);
        }
        finally
        {
            File.Delete(certificatePath);
        }
    }

    [Fact]
    public void ProductionConfigurationRequiresLocalAdministratorAndConnectedProfile()
    {
        var environment = new StubHostEnvironment("Production", Path.GetTempPath());
        var disabledAdministrator = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Edge:LocalAdmin:AuthenticationEnabled"] = "false",
                ["Edge:Integrations:Profile"] = "Connected"
            }).Build();
        var mockProfile = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Edge:LocalAdmin:AuthenticationEnabled"] = "true",
                ["Edge:Integrations:Profile"] = "Mock"
            }).Build();
        var valid = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Edge:LocalAdmin:AuthenticationEnabled"] = "true",
                ["Edge:Integrations:Profile"] = "Connected"
            }).Build();

        Assert.Throws<InvalidOperationException>(() =>
            EdgeProductionConfiguration.Validate(disabledAdministrator, environment));
        Assert.Throws<InvalidOperationException>(() =>
            EdgeProductionConfiguration.Validate(mockProfile, environment));
        EdgeProductionConfiguration.Validate(valid, environment);
    }

    [Fact]
    public void TrustedProxyConfigurationRequiresExplicitValidAddresses()
    {
        var configured = EdgeForwardedHeaders.Create(new EdgeTrustedProxyOptions
        {
            Enabled = true,
            ForwardLimit = 2,
            KnownProxies = ["10.0.0.10"],
            KnownNetworks = ["192.0.2.0/24"]
        });

        Assert.Equal(2, configured.ForwardLimit);
        Assert.Single(configured.KnownProxies);
        Assert.Single(configured.KnownIPNetworks);
        Assert.Throws<InvalidOperationException>(() => EdgeForwardedHeaders.Create(
            new EdgeTrustedProxyOptions { Enabled = true }));
        Assert.Throws<InvalidOperationException>(() => EdgeForwardedHeaders.Create(
            new EdgeTrustedProxyOptions { Enabled = true, KnownProxies = ["not-an-address"] }));
        Assert.Throws<InvalidOperationException>(() => EdgeForwardedHeaders.Create(
            new EdgeTrustedProxyOptions { Enabled = true, KnownNetworks = ["not-a-network"] }));
    }

    [Fact]
    public async Task AdministratorBackupEndpointCreatesOnlyEncryptedBackupsAndHidesServerPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"post-edge-backup-api-{Guid.NewGuid():N}");
        var variable = $"POST_EDGE_API_STORAGE_KEY_{Guid.NewGuid():N}";
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable(variable, new string('E', 48));
        try
        {
            await using var factory = new EdgeApiFactory(new Dictionary<string, string?>
            {
                ["Edge:Storage:EncryptionEnabled"] = "true",
                ["Edge:Storage:EncryptionKeyEnvironmentVariable"] = variable,
                ["Edge:Storage:BackupDirectory"] = root
            });
            using var client = factory.CreateClient();
            using var response = await client.PostAsync(new Uri("/admin/storage/backup", UriKind.Relative), content: null, cancellationToken: TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("\"encrypted\":true", body, StringComparison.Ordinal);
            Assert.DoesNotContain(root, body, StringComparison.OrdinalIgnoreCase);
            Assert.Single(Directory.EnumerateFiles(root, "*.db"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StatusAndReadinessExposeUnresolvedSagaCountWithoutPayload()
    {
        await using var factory = new EdgeApiFactory();
        using var client = factory.CreateClient();
        using (var warmup = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.OK, warmup.StatusCode);
        var operationId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IExternalOperationSagaRepository>().AddIntent(
                new ExternalOperationSaga(
                    operationId,
                    "bag.close",
                    "HASH",
                    "{\"sensitive\":\"must-not-leak\"}",
                    null,
                    false,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch));
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var status = await client.GetAsync(new Uri("/api/edge/status", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var readiness = await factory.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration =>
                registration.Name == EdgeReadinessHealthCheck.RegistrationName, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Contains("\"unresolvedExternalOperationCount\":1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", body, StringComparison.Ordinal);
        Assert.Equal(HealthStatus.Degraded, readiness.Status);
        Assert.DoesNotContain("must-not-leak", readiness.Entries.Values.Single().Description,
            StringComparison.Ordinal);
    }

    private sealed class UnavailableConfigurationSource : EdgeConfigurationSource
    {
        public Task<EdgeConfigurationDto?> FetchAsync(long? currentVersion, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("Configuration source is unavailable.");
        }
    }

    private sealed class StubHostEnvironment(string environmentName, string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Post.Edge.Api.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
