using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Post.Edge.Application;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Integrations.Core;
using Post.Edge.Infrastructure.Integrations.Destination;
using Post.Edge.Infrastructure.Integrations.Mock;
using Post.Edge.Infrastructure.Integrations.Postal;

namespace Post.Edge.Infrastructure.Tests;

public sealed class DependencyInjectionModeTests
{
    public static TheoryData<string> RealHttpPathSettings { get; } = new()
    {
        "Edge:DestinationApi:TokenPath",
        "Edge:DestinationApi:ParcelInfoPath",
        "Edge:CoreApi:InboundLookupPath",
        "Edge:CoreApi:ParcelEventsPath",
        "Edge:CoreApi:BagsPath",
        "Edge:CoreApi:DispatchesPath",
        "Edge:CoreApi:EventsPath",
        "Edge:CoreApi:OperationalResultsPath",
        "Edge:CoreApi:RegistrationPath",
        "Edge:CoreApi:HeartbeatPath",
        "Edge:CoreApi:BootstrapPath",
        "Edge:CoreApi:ImagePresignedPath",
        "Edge:CoreApi:ImageMetadataPath",
        "Edge:CoreApi:TokenPath"
    };

    [Fact]
    public async Task LocalConnectedUsesRealDestinationWithMockPostalAndCore()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Edge:Integrations:Profile"] = "LocalConnected",
            ["Edge:Security:AllowUnboundDevices"] = "true",
            ["Edge:DestinationApi:BaseAddress"] = "https://destination.test",
            ["Edge:DestinationApi:Username"] = "user",
            ["Edge:DestinationApi:Password"] = "password"
        });
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<HttpDestinationLookupClient>(scope.ServiceProvider.GetRequiredService<IDestinationLookupClient>());
        Assert.IsType<MockPostalInboundClient>(scope.ServiceProvider.GetRequiredService<IPostalInboundClient>());
        Assert.IsType<MockCoreApiClient>(scope.ServiceProvider.GetRequiredService<ICoreApiClient>());
        Assert.True(scope.ServiceProvider.GetRequiredService<IDeviceNetworkPolicy>().AllowDeviceWithoutBinding);
        Assert.Contains(
            provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations,
            registration => registration.Name == DestinationOAuthHealthCheck.RegistrationName);
    }

    [Fact]
    public async Task ConnectedUsesEveryRealAdapterAndAlwaysRequiresNetworkBinding()
    {
        await using var provider = BuildProvider(ConnectedSettings());
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<HttpDestinationLookupClient>(scope.ServiceProvider.GetRequiredService<IDestinationLookupClient>());
        Assert.IsType<SqlPostalInboundClient>(scope.ServiceProvider.GetRequiredService<IPostalInboundClient>());
        Assert.IsType<PostalSoapClient>(scope.ServiceProvider.GetRequiredService<IParcelInfoClient>());
        Assert.IsType<CoreApiClient>(scope.ServiceProvider.GetRequiredService<ICoreApiClient>());
        Assert.IsType<CoreConfigurationSource>(
            scope.ServiceProvider.GetRequiredService<Post.Edge.Application.Ports.IConfigurationSource>());
        Assert.False(scope.ServiceProvider.GetRequiredService<IDeviceNetworkPolicy>().AllowDeviceWithoutBinding);
        var clients = provider.GetRequiredService<IHttpClientFactory>();
        using var destinationClient = clients.CreateClient("DestinationApi");
        using var coreAuthenticationClient = clients.CreateClient("CoreAuthentication");
        using var coreClient = clients.CreateClient(typeof(CoreApiClient).FullName!);
        using var soapClient = clients.CreateClient(typeof(PostalSoapClient).FullName!);
        Assert.Equal(Timeout.InfiniteTimeSpan, destinationClient.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, coreAuthenticationClient.Timeout);
        // The standard resilience pipeline owns the 15-second total timeout. The typed-client
        // factory's diagnostic named client may retain HttpClient's bounded 100-second guard.
        Assert.InRange(coreClient.Timeout, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(100));
        Assert.InRange(soapClient.Timeout, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(100));
    }

    [Fact]
    public async Task CustomProfileAllowsIndependentAdapterSelection()
    {
        await using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Edge:Integrations:Profile"] = "Custom",
            ["Edge:Integrations:Destination"] = "Mock",
            ["Edge:Integrations:Postal"] = "Real",
            ["Edge:Integrations:Core"] = "Mock",
            ["Edge:PostalSql:ConnectionString"] = "Server=postal.test;Database=Postal;Encrypt=true",
            ["Edge:PostalSql:UserId"] = "1",
            ["Edge:PostalSql:SourceId"] = "59544",
            ["Edge:PostalSql:PostUsername"] = "edge",
            ["Edge:PostalSoap:Endpoint"] = "https://postal.test/soap"
        });
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<MockDestinationLookupClient>(scope.ServiceProvider.GetRequiredService<IDestinationLookupClient>());
        Assert.IsType<SqlPostalBagClient>(scope.ServiceProvider.GetRequiredService<IPostalBagClient>());
        Assert.IsType<MockCoreApiClient>(scope.ServiceProvider.GetRequiredService<ICoreApiClient>());
    }

    [Fact]
    public void ConnectedFailsFastForIncompleteConfigurationWithoutEchoingSecrets()
    {
        const string sensitiveValue = "do-not-echo";
        var values = new Dictionary<string, string?>
        {
            ["Edge:Integrations:Profile"] = "Connected",
            ["Edge:DestinationApi:Username"] = sensitiveValue
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddEdgeApplication();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddEdgeInfrastructure(configuration));

        Assert.DoesNotContain(sensitiveValue, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Real Edge adapter requires", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LocalConnected", "Destination", "Edge:DestinationApi:BaseAddress")]
    [InlineData("Custom", "Destination", "Edge:DestinationApi:BaseAddress")]
    [InlineData("Custom", "Core", "Edge:CoreApi:BaseAddress")]
    [InlineData("Custom", "Postal", "Edge:PostalSoap:Endpoint")]
    public void EveryRealCredentialBearingAdapterRequiresHttps(
        string profile,
        string realAdapter,
        string insecureSetting)
    {
        var values = RealAdapterSettings(profile, realAdapter);
        values[insecureSetting] = "http://insecure.test/path";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(Configuration(values)));

        Assert.Contains("secure endpoint", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("insecure.test", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RealHttpPathSettings))]
    public void RealAdapterPathsCannotEscapeConfiguredOrigin(string key)
    {
        var values = ConnectedSettings();
        values[key] = key.EndsWith("TokenPath", StringComparison.Ordinal)
            ? "http://attacker.test/token"
            : "https://other-host.test/path";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(Configuration(values)));

        Assert.Contains("root-relative", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.test", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("other-host.test", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Edge:DestinationApi:BaseAddress", "https://user:password@destination.test")]
    [InlineData("Edge:CoreApi:BaseAddress", "https://core.test?redirect=https://attacker.test")]
    [InlineData("Edge:PostalSoap:Endpoint", "https://postal.test/soap#fragment")]
    public void RealEndpointsRejectUserInfoQueryAndFragment(string key, string unsafeUri)
    {
        var values = ConnectedSettings();
        values[key] = unsafeUri;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(Configuration(values)));

        Assert.Contains("secure endpoint", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("attacker.test", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Edge:DestinationApi:RequestTimeout")]
    [InlineData("Edge:CoreApi:RequestTimeout")]
    [InlineData("Edge:PostalSoap:RequestTimeout")]
    public void RealAdapterTimeoutsAreBounded(string key)
    {
        var values = ConnectedSettings();
        values[key] = "00:00:00.500";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(Configuration(values)));

        Assert.Contains("valid value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PostalSqlAndSoapHeadersRequireSecureBoundedConfiguration()
    {
        var trustServer = ConnectedSettings();
        trustServer["Edge:PostalSql:ConnectionString"] =
            "Server=postal.test;Database=Postal;Encrypt=true;TrustServerCertificate=true";
        var unencrypted = ConnectedSettings();
        unencrypted["Edge:PostalSql:ConnectionString"] =
            "Server=postal.test;Database=Postal;Encrypt=false;TrustServerCertificate=false";
        var unboundedCommand = ConnectedSettings();
        unboundedCommand["Edge:PostalSql:CommandTimeoutSeconds"] = "121";
        var injectedAction = ConnectedSettings();
        injectedAction["Edge:PostalSoap:SoapAction"] = "safe\r\nInjected: value";

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(Configuration(trustServer)));
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(Configuration(unencrypted)));
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(Configuration(unboundedCommand)));
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(Configuration(injectedAction)));
    }

    [Fact]
    public void InvalidOrIncompleteCustomProfileFailsFast()
    {
        var invalidProfile = Configuration(new Dictionary<string, string?>
        {
            ["Edge:Integrations:Profile"] = "invalid"
        });
        var incompleteCustom = Configuration(new Dictionary<string, string?>
        {
            ["Edge:Integrations:Profile"] = "Custom",
            ["Edge:Integrations:Destination"] = "Mock"
        });

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(invalidProfile));
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(incompleteCustom));
    }

    [Fact]
    public async Task DevelopmentMayExplicitlyAllowOnlyNamedInsecureIntegrationHost()
    {
        var values = RealAdapterSettings("CoreConnected", "Core");
        values["Edge:CoreApi:BaseAddress"] = "http://gateway:8080";
        values["Edge:Security:AllowInsecureDevelopmentIntegrations"] = "true";
        values["Edge:Security:AllowedInsecureDevelopmentHosts:0"] = "gateway";

        await using var provider = BuildProvider(values, new StubHostEnvironment("Development"));
        await using var scope = provider.CreateAsyncScope();

        Assert.IsType<CoreApiClient>(scope.ServiceProvider.GetRequiredService<ICoreApiClient>());
    }

    [Fact]
    public void InsecureIntegrationOptInNeverAppliesInProductionOrToUnlistedHost()
    {
        var values = RealAdapterSettings("CoreConnected", "Core");
        values["Edge:CoreApi:BaseAddress"] = "http://gateway:8080";
        values["Edge:Security:AllowInsecureDevelopmentIntegrations"] = "true";
        values["Edge:Security:AllowedInsecureDevelopmentHosts:0"] = "other-host";

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(
                Configuration(values),
                new StubHostEnvironment("Development")));
        values["Edge:Security:AllowedInsecureDevelopmentHosts:0"] = "gateway";
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(
                Configuration(values),
                new StubHostEnvironment("Production")));
    }

    [Fact]
    public void ProductionRequiresEncryptedFileBackedStorage()
    {
        var values = new Dictionary<string, string?>
        {
            ["Edge:Integrations:Profile"] = "Mock",
            ["Edge:Storage:ConnectionString"] = "Data Source=edge-production.db",
            ["Edge:Storage:EncryptionEnabled"] = "false"
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(
                Configuration(values),
                new StubHostEnvironment("Production")));

        Assert.Contains("encryption at rest", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EncryptedStorageRequiresExternalKeyAndRejectsUnsafeConnectionSettings()
    {
        var variable = $"POST_EDGE_MISSING_TEST_KEY_{Guid.NewGuid():N}";
        var values = new Dictionary<string, string?>
        {
            ["Edge:Integrations:Profile"] = "Mock",
            ["Edge:Storage:ConnectionString"] = "Data Source=edge-encrypted.db",
            ["Edge:Storage:EncryptionEnabled"] = "true",
            ["Edge:Storage:EncryptionKeyEnvironmentVariable"] = variable
        };

        var missing = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddEdgeInfrastructure(Configuration(values)));
        Assert.DoesNotContain(variable, missing.Message, StringComparison.Ordinal);

        Environment.SetEnvironmentVariable(variable, new string('S', 48));
        try
        {
            values["Edge:Storage:ConnectionString"] = "Data Source=edge-encrypted.db;Cache=Shared";
            Assert.Throws<InvalidOperationException>(() =>
                new ServiceCollection().AddEdgeInfrastructure(Configuration(values)));
            values["Edge:Storage:ConnectionString"] = "Data Source=edge-encrypted.db;Password=inline";
            Assert.Throws<InvalidOperationException>(() =>
                new ServiceCollection().AddEdgeInfrastructure(Configuration(values)));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static ServiceProvider BuildProvider(
        IReadOnlyDictionary<string, string?> values,
        IHostEnvironment? environment = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEdgeApplication();
        services.AddEdgeInfrastructure(Configuration(values), environment);
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static Dictionary<string, string?> ConnectedSettings() => new()
    {
        ["Edge:Integrations:Profile"] = "Connected",
        ["Edge:Security:AllowUnboundDevices"] = "true",
        ["Edge:DestinationApi:BaseAddress"] = "https://destination.test",
        ["Edge:DestinationApi:Username"] = "user",
        ["Edge:DestinationApi:Password"] = "password",
        ["Edge:CoreApi:BaseAddress"] = "https://core.test",
        ["Edge:CoreApi:EdgeId"] = "11111111-1111-1111-1111-111111111111",
        ["Edge:CoreApi:ExchangeCenterCode"] = "59544",
        ["Edge:CoreApi:LogicalDeviceCode"] = "edge-01",
        ["Edge:CoreApi:DeviceSecret"] = "credential",
        ["Edge:PostalSql:ConnectionString"] = "Server=postal.test;Database=Postal;Encrypt=true",
        ["Edge:PostalSql:UserId"] = "1",
        ["Edge:PostalSql:SourceId"] = "59544",
        ["Edge:PostalSql:PostUsername"] = "edge",
        ["Edge:PostalSoap:Endpoint"] = "https://postal.test/soap"
    };

    private static Dictionary<string, string?> RealAdapterSettings(string profile, string adapter)
    {
        var values = ConnectedSettings();
        values["Edge:Integrations:Profile"] = profile;
        values["Edge:Integrations:Destination"] = "Mock";
        values["Edge:Integrations:Postal"] = "Mock";
        values["Edge:Integrations:Core"] = "Mock";
        values[$"Edge:Integrations:{adapter}"] = "Real";
        return values;
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Post.Edge.Infrastructure.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
