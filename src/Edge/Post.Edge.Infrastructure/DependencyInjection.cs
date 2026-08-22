using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Concurrency;
using Post.Edge.Infrastructure.Integrations.Core;
using Post.Edge.Infrastructure.Integrations.Destination;
using Post.Edge.Infrastructure.Integrations.Mock;
using Post.Edge.Infrastructure.Integrations.Postal;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Persistence;
using Post.Edge.Infrastructure.Resilience;
using Post.Edge.Infrastructure.Workers;
using EdgeConfigurationSource = Post.Edge.Application.Ports.IConfigurationSource;

namespace Post.Edge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEdgeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        EdgeSqliteRuntime.EnsureInitialized();

        services.TryAddSingleton(configuration);

        services.Configure<EdgeIntegrationOptions>(configuration.GetSection(EdgeIntegrationOptions.SectionName));
        services.Configure<EdgeStorageOptions>(configuration.GetSection(EdgeStorageOptions.SectionName));
        services.Configure<EdgeWorkerOptions>(configuration.GetSection(EdgeWorkerOptions.SectionName));
        services.Configure<EdgeMockOptions>(configuration.GetSection(EdgeMockOptions.SectionName));
        services.Configure<EdgeSecurityOptions>(configuration.GetSection(EdgeSecurityOptions.SectionName));
        services.Configure<EdgeLocalAdministratorOptions>(
            configuration.GetSection(EdgeLocalAdministratorOptions.SectionName));
        services.Configure<EdgeAutoDispatchOptions>(configuration.GetSection(EdgeAutoDispatchOptions.SectionName));
        services.Configure<DestinationApiOptions>(configuration.GetSection(DestinationApiOptions.SectionName));
        services.Configure<CoreApiOptions>(configuration.GetSection(CoreApiOptions.SectionName));
        services.Configure<PostalSqlOptions>(configuration.GetSection(PostalSqlOptions.SectionName));
        services.Configure<PostalSoapOptions>(configuration.GetSection(PostalSoapOptions.SectionName));
        services.Configure<EdgeResilienceOptions>(configuration.GetSection(EdgeResilienceOptions.SectionName));

        var storage = configuration.GetSection(EdgeStorageOptions.SectionName).Get<EdgeStorageOptions>()
                      ?? new EdgeStorageOptions();
        ValidateStorageConfiguration(storage, environment);
        services.AddSingleton<IEdgeStorageKeyProvider, EnvironmentEdgeStorageKeyProvider>();
        services.AddSingleton<IEdgeStorageMaintenance, EdgeStorageMaintenance>();
        services.AddDbContext<EdgeDbContext>((provider, builder) =>
        {
            var keys = storage.EncryptionEnabled
                ? provider.GetRequiredService<IEdgeStorageKeyProvider>().GetRequiredKeys()
                : null;
            builder.UseSqlite(EdgeStorageConnection.Build(storage, keys));
        });
        services.AddScoped<IEdgeUnitOfWork>(x => x.GetRequiredService<EdgeDbContext>());
        services.AddScoped<IEdgeParcelRepository, ParcelRepository>();
        services.AddScoped<IEdgeBagRepository, BagRepository>();
        services.AddScoped<IPendingBagClosureRepository, PendingBagClosureRepository>();
        services.AddScoped<IDispatchRepository, DispatchRepository>();
        services.AddScoped<IPendingInboundRepository, PendingInboundRepository>();
        services.AddScoped<IInboundEvidenceRepository, InboundEvidenceRepository>();
        services.AddScoped<IDeviceConfigurationRepository, DeviceConfigurationRepository>();
        services.AddScoped<IDeviceSessionStore, DeviceSessionStore>();
        services.AddScoped<IDeviceNetworkBindingRepository, DeviceNetworkBindingRepository>();
        services.AddScoped<ILocalAdministratorRepository, LocalAdministratorRepository>();
        services.AddScoped<IOperationJournal, OperationJournal>();
        services.AddScoped<IExternalOperationSagaRepository, ExternalOperationSagaRepository>();
        services.AddSingleton<IOutboxWakeSignal, OutboxWakeSignal>();
        services.AddScoped<OutboxWriter>();
        services.AddScoped<IOutboxWriter>(provider => provider.GetRequiredService<OutboxWriter>());
        services.AddScoped<IOutboxStatus>(provider => provider.GetRequiredService<OutboxWriter>());
        services.AddSingleton<IDestinationLockProvider, InMemoryDestinationLockProvider>();
        services.AddSingleton<IBagLabelGenerator, BasicBagLabelGenerator>();
        services.AddSingleton<IDiskSpaceProbe, EdgeDiskSpaceProbe>();
        services.TryAddSingleton<IDeviceEventPublisher, InMemoryDeviceEventPublisher>();
        services.AddHealthChecks().AddCheck<EdgeReadinessHealthCheck>(
            EdgeReadinessHealthCheck.RegistrationName,
            tags: ["ready", "storage", "configuration"]);

        var integrations = EdgeIntegrationSelection.Resolve(configuration);
        ValidateWorkerConfiguration(configuration);
        var resilience = ValidateResilienceConfiguration(configuration);
        ValidateLocalAdministratorConfiguration(configuration);
        ValidateSelectedIntegrations(configuration, integrations, environment);
        services.AddSingleton(integrations);
        services.AddSingleton<IDeviceNetworkPolicy>(provider => new DeviceNetworkPolicy(
            provider.GetRequiredService<IOptions<EdgeSecurityOptions>>(),
            integrations.IsDevelopmentProfile));

        if (integrations.Destination == EdgeAdapterMode.Real)
        {
            var destination = configuration.GetSection(DestinationApiOptions.SectionName).Get<DestinationApiOptions>()
                              ?? new DestinationApiOptions();
            AddRealDestination(services, resilience, destination.RequestTimeout);
        }
        else AddMockDestination(services);
        if (integrations.Postal == EdgeAdapterMode.Real)
        {
            var postalSoap = configuration.GetSection(PostalSoapOptions.SectionName).Get<PostalSoapOptions>()
                             ?? new PostalSoapOptions();
            AddRealPostal(services, resilience, postalSoap.RequestTimeout);
        }
        else AddMockPostal(services);
        if (integrations.Core == EdgeAdapterMode.Real)
        {
            var core = configuration.GetSection(CoreApiOptions.SectionName).Get<CoreApiOptions>()
                       ?? new CoreApiOptions();
            AddRealCore(services, resilience, core.RequestTimeout);
        }
        else AddMockCore(services);

        services.AddScoped<OutboxProcessor>();
        services.AddScoped<DeadLetterService>();
        services.AddScoped<AutoDispatchService>();
        services.AddScoped<LocalCleanupService>();
        services.AddHostedService<EdgeDatabaseInitializer>();
        services.AddHostedService<LocalAdministratorBootstrapHostedService>();
        services.AddHostedService<OutboxWorker>();
        services.AddHostedService<PendingWorkWorker>();
        services.AddHostedService<AutoDispatchWorker>();
        services.AddHostedService<LocalCleanupWorker>();
        if (integrations.Core == EdgeAdapterMode.Real)
        {
            services.AddHostedService<ConfigurationPollingWorker>();
            services.AddHostedService<CoreEdgeHealthWorker>();
        }
        return services;
    }

    private static void AddMockDestination(IServiceCollection services) =>
        services.AddSingleton<IDestinationLookupClient, MockDestinationLookupClient>();

    private static void AddMockPostal(IServiceCollection services)
    {
        services.AddSingleton<IPostalInboundClient, MockPostalInboundClient>();
        services.AddSingleton<IPostalBagClient, MockPostalBagClient>();
        services.AddSingleton<IPostalDispatchClient, MockPostalDispatchClient>();
        services.AddSingleton<IParcelInfoClient, MockParcelInfoClient>();
    }

    private static void AddMockCore(IServiceCollection services)
    {
        services.AddSingleton<MockCoreApiClient>();
        services.AddSingleton<ICoreApiClient>(provider => provider.GetRequiredService<MockCoreApiClient>());
        services.AddSingleton<ICoreEdgeHealthClient>(provider => provider.GetRequiredService<MockCoreApiClient>());
        services.AddSingleton<EdgeConfigurationSource, MockConfigurationSource>();
        services.AddSingleton<ICoreImageUploadClient, MockCoreImageUploadClient>();
    }

    private static void AddRealDestination(
        IServiceCollection services,
        EdgeResilienceOptions resilience,
        TimeSpan totalTimeout)
    {
        var builder = services.AddHttpClient("DestinationApi", (provider, client) =>
        {
            var settings = provider.GetRequiredService<IOptions<DestinationApiOptions>>().Value;
            client.BaseAddress = new Uri(settings.BaseAddress, UriKind.Absolute);
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        EdgeHttpResilience.AddStandard(builder, resilience, totalTimeout);
        services.AddSingleton(provider => new DestinationTokenCache(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("DestinationApi"),
            provider.GetRequiredService<IOptions<DestinationApiOptions>>(),
            provider.GetRequiredService<IClock>()));
        services.AddScoped<IDestinationLookupClient>(provider => new HttpDestinationLookupClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("DestinationApi"),
            provider.GetRequiredService<DestinationTokenCache>(),
            provider.GetRequiredService<IOptions<DestinationApiOptions>>()));
        services.AddHealthChecks().AddCheck<DestinationOAuthHealthCheck>(
            DestinationOAuthHealthCheck.RegistrationName,
            tags: ["ready", "external", "destination"]);
    }

    private static void AddRealCore(
        IServiceCollection services,
        EdgeResilienceOptions resilience,
        TimeSpan totalTimeout)
    {
        EdgeHttpResilience.AddStandard(
            services.AddHttpClient("CoreAuthentication", ConfigureCoreHttpClient),
            resilience,
            totalTimeout);
        services.AddSingleton<ICoreAccessTokenProvider>(provider => new CoreAccessTokenProvider(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("CoreAuthentication"),
            provider.GetRequiredService<IOptions<CoreApiOptions>>(),
            provider.GetRequiredService<IClock>()));
        EdgeHttpResilience.AddStandard(
            services.AddHttpClient<CoreApiClient>(ConfigureCoreHttpClient), resilience, totalTimeout);
        EdgeHttpResilience.AddStandard(
            services.AddHttpClient<CoreConfigurationSource>(ConfigureCoreHttpClient), resilience, totalTimeout);
        EdgeHttpResilience.AddStandard(
            services.AddHttpClient<CoreImageUploadClient>(ConfigureCoreHttpClient), resilience, totalTimeout);
        services.AddScoped<ICoreApiClient>(x => x.GetRequiredService<CoreApiClient>());
        services.AddScoped<ICoreEdgeHealthClient>(x => x.GetRequiredService<CoreApiClient>());
        services.AddScoped<ICoreImageUploadClient>(x => x.GetRequiredService<CoreImageUploadClient>());
        services.AddScoped<EdgeConfigurationSource>(x => x.GetRequiredService<CoreConfigurationSource>());
    }

    private static void AddRealPostal(
        IServiceCollection services,
        EdgeResilienceOptions resilience,
        TimeSpan totalTimeout)
    {
        EdgeHttpResilience.AddStandard(
            services.AddHttpClient<PostalSoapClient>(static client => client.Timeout = Timeout.InfiniteTimeSpan),
            resilience,
            totalTimeout);
        services.AddScoped<IParcelInfoClient>(x => x.GetRequiredService<PostalSoapClient>());
        services.AddSingleton<IPostalSqlResilienceExecutor, PostalSqlResilienceExecutor>();
        services.AddScoped<IPostalSqlGateway, PostalSqlGateway>();
        services.AddScoped<IPostalInboundClient, SqlPostalInboundClient>();
        services.AddScoped<IPostalBagClient, SqlPostalBagClient>();
        services.AddScoped<IPostalDispatchClient, SqlPostalDispatchClient>();
    }

    private static void ConfigureCoreHttpClient(IServiceProvider provider, HttpClient client)
    {
        var settings = provider.GetRequiredService<IOptions<CoreApiOptions>>().Value;
        client.BaseAddress = new Uri(settings.BaseAddress, UriKind.Absolute);
        client.Timeout = Timeout.InfiniteTimeSpan;
    }

    private static void ValidateSelectedIntegrations(
        IConfiguration configuration,
        EdgeIntegrationSelection integrations,
        IHostEnvironment? environment)
    {
        var security = configuration.GetSection(EdgeSecurityOptions.SectionName).Get<EdgeSecurityOptions>()
                       ?? new EdgeSecurityOptions();
        ValidateInsecureDevelopmentHosts(security);
        if (integrations.Destination == EdgeAdapterMode.Real)
            ValidateRealDestination(configuration, security, environment);
        if (integrations.Core == EdgeAdapterMode.Real)
            ValidateRealCore(
                configuration,
                integrations.IsDevelopmentProfile || IsDevelopmentEnvironment(environment),
                security,
                environment);
        if (integrations.Postal == EdgeAdapterMode.Real)
            ValidateRealPostal(configuration, security, environment);
    }

    private static void ValidateRealDestination(
        IConfiguration configuration,
        EdgeSecurityOptions security,
        IHostEnvironment? environment)
    {
        var destination = configuration.GetSection(DestinationApiOptions.SectionName).Get<DestinationApiOptions>()
                          ?? new DestinationApiOptions();
        RequireSecureEndpoint(destination.BaseAddress, "Edge:DestinationApi:BaseAddress", security, environment);
        RequireRootRelativePath(destination.TokenPath, "Edge:DestinationApi:TokenPath");
        RequireRootRelativePath(destination.ParcelInfoPath, "Edge:DestinationApi:ParcelInfoPath");
        RequireValue(destination.Username, "Edge:DestinationApi:Username");
        RequireValue(destination.Password, "Edge:DestinationApi:Password");
        if (destination.TokenLifetime <= TimeSpan.Zero ||
            destination.RefreshSkew < TimeSpan.Zero ||
            destination.RefreshSkew >= destination.TokenLifetime)
        {
            throw InvalidRealSetting("Edge:DestinationApi token lifetime/refresh skew");
        }

        RequireRequestTimeout(destination.RequestTimeout, "Edge:DestinationApi:RequestTimeout");
        RequireResponseLimit(destination.MaximumResponseBytes, "Edge:DestinationApi:MaximumResponseBytes");
    }

    private static void ValidateRealCore(
        IConfiguration configuration,
        bool developmentProfile,
        EdgeSecurityOptions security,
        IHostEnvironment? environment)
    {
        var core = configuration.GetSection(CoreApiOptions.SectionName).Get<CoreApiOptions>() ?? new CoreApiOptions();
        RequireSecureEndpoint(core.BaseAddress, "Edge:CoreApi:BaseAddress", security, environment);
        if (core.EdgeId == Guid.Empty) throw InvalidRealSetting("Edge:CoreApi:EdgeId");
        RequireValue(core.ExchangeCenterCode, "Edge:CoreApi:ExchangeCenterCode");
        RequireRootRelativePath(core.InboundLookupPath, "Edge:CoreApi:InboundLookupPath");
        RequireRootRelativePath(core.ParcelEventsPath, "Edge:CoreApi:ParcelEventsPath");
        RequireRootRelativePath(core.BagsPath, "Edge:CoreApi:BagsPath");
        RequireRootRelativePath(core.DispatchesPath, "Edge:CoreApi:DispatchesPath");
        RequireRootRelativePath(core.EventsPath, "Edge:CoreApi:EventsPath");
        RequireRootRelativePath(core.OperationalResultsPath, "Edge:CoreApi:OperationalResultsPath");
        RequireRootRelativePath(core.RegistrationPath, "Edge:CoreApi:RegistrationPath");
        RequireRootRelativePath(core.HeartbeatPath, "Edge:CoreApi:HeartbeatPath");
        RequireRootRelativePath(core.BootstrapPath, "Edge:CoreApi:BootstrapPath");
        RequireRootRelativePath(core.ImagePresignedPath, "Edge:CoreApi:ImagePresignedPath");
        RequireRootRelativePath(core.ImageMetadataPath, "Edge:CoreApi:ImageMetadataPath");
        RequireRootRelativePath(core.TokenPath, "Edge:CoreApi:TokenPath");
        RequireRequestTimeout(core.RequestTimeout, "Edge:CoreApi:RequestTimeout");
        RequireResponseLimit(core.MaximumResponseBytes, "Edge:CoreApi:MaximumResponseBytes");

        if (core.UseFixedBearerToken)
        {
            if (!developmentProfile)
                throw new InvalidOperationException(
                    "A fixed Core bearer-token override is permitted only by an explicit development profile.");
            RequireValue(core.BearerToken, "Edge:CoreApi:BearerToken");
        }
        else
        {
            RequireValue(core.LogicalDeviceCode, "Edge:CoreApi:LogicalDeviceCode");
            RequireValue(core.DeviceSecret, "Edge:CoreApi:DeviceSecret");
        }

        if (core.TokenRefreshSkew < TimeSpan.Zero ||
            core.DefaultTokenLifetime <= TimeSpan.Zero ||
            core.TokenRefreshSkew >= core.DefaultTokenLifetime)
        {
            throw InvalidRealSetting("Edge:CoreApi token lifetime/refresh skew");
        }
    }

    private static void ValidateRealPostal(
        IConfiguration configuration,
        EdgeSecurityOptions security,
        IHostEnvironment? environment)
    {
        var sql = configuration.GetSection(PostalSqlOptions.SectionName).Get<PostalSqlOptions>() ?? new PostalSqlOptions();
        RequireValue(sql.ConnectionString, "Edge:PostalSql:ConnectionString");
        RequireValue(sql.PostUsername, "Edge:PostalSql:PostUsername");
        if (sql.UserId <= 0) throw InvalidRealSetting("Edge:PostalSql:UserId");
        if (sql.SourceId <= 0) throw InvalidRealSetting("Edge:PostalSql:SourceId");
        if (sql.CommandTimeoutSeconds is < 1 or > 120)
            throw InvalidRealSetting("Edge:PostalSql:CommandTimeoutSeconds");
        Microsoft.Data.SqlClient.SqlConnectionStringBuilder sqlConnection;
        try
        {
            sqlConnection = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sql.ConnectionString);
        }
        catch (ArgumentException)
        {
            throw InvalidRealSetting("Edge:PostalSql:ConnectionString");
        }
        if (string.IsNullOrWhiteSpace(sqlConnection.DataSource) ||
            !sqlConnection.Encrypt ||
            sqlConnection.TrustServerCertificate ||
            sqlConnection.ConnectTimeout is < 1 or > 60)
        {
            throw InvalidRealSetting(
                "Edge:PostalSql:ConnectionString (encrypted transport, certificate validation, and timeout)");
        }

        var soap = configuration.GetSection(PostalSoapOptions.SectionName).Get<PostalSoapOptions>() ?? new PostalSoapOptions();
        RequireSecureEndpoint(soap.Endpoint, "Edge:PostalSoap:Endpoint", security, environment);
        RequireSafeHeaderValue(soap.SoapAction, "Edge:PostalSoap:SoapAction");
        RequireRequestTimeout(soap.RequestTimeout, "Edge:PostalSoap:RequestTimeout");
        if (soap.MaximumResponseCharacters <= 0 || soap.MaximumResponseCharacters > 8_388_608)
            throw InvalidRealSetting("Edge:PostalSoap:MaximumResponseCharacters");
    }

    private static void RequireSecureEndpoint(
        string? value,
        string key,
        EdgeSecurityOptions security,
        IHostEnvironment? environment)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException($"A Real Edge adapter requires a valid secure endpoint for {key}.");
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return;
        var explicitlyAllowedDevelopmentHost =
            string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            IsDevelopmentEnvironment(environment) &&
            security.AllowInsecureDevelopmentIntegrations &&
            (uri.IsLoopback || security.AllowedInsecureDevelopmentHosts.Contains(
                uri.IdnHost,
                StringComparer.OrdinalIgnoreCase));
        if (!explicitlyAllowedDevelopmentHost)
            throw new InvalidOperationException($"A Real Edge adapter requires a valid secure endpoint for {key}.");
    }

    private static bool IsDevelopmentEnvironment(IHostEnvironment? environment) =>
        environment is not null &&
        (environment.IsDevelopment() || environment.IsEnvironment("Testing"));

    private static void ValidateInsecureDevelopmentHosts(EdgeSecurityOptions security)
    {
        if (security.AllowedInsecureDevelopmentHosts.Count > 32 ||
            security.AllowedInsecureDevelopmentHosts.Any(host =>
                string.IsNullOrWhiteSpace(host) ||
                host.Length > 253 ||
                host.Contains('*', StringComparison.Ordinal) ||
                host.Contains('/', StringComparison.Ordinal) ||
                host.Contains(':', StringComparison.Ordinal) ||
                host.Any(char.IsControl)))
        {
            throw new InvalidOperationException("Edge insecure-development host allow-list is invalid.");
        }
    }

    private static void RequireRootRelativePath(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value[0] != '/' ||
            value.StartsWith("//", StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            value.Contains('?', StringComparison.Ordinal) ||
            value.Contains('#', StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new InvalidOperationException(
                $"A Real Edge adapter requires {key} to be a root-relative path without an authority or query.");
        }
    }

    private static void RequireResponseLimit(int value, string key)
    {
        if (value <= 0 || value > 33_554_432)
            throw InvalidRealSetting(key);
    }

    private static void RequireRequestTimeout(TimeSpan value, string key)
    {
        if (value < TimeSpan.FromSeconds(1) || value > TimeSpan.FromMinutes(2))
            throw InvalidRealSetting(key);
    }

    private static void RequireSafeHeaderValue(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            throw InvalidRealSetting(key);
    }

    private static void RequireValue(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) throw InvalidRealSetting(key);
    }

    private static InvalidOperationException InvalidRealSetting(string key) =>
        new($"A Real Edge adapter requires a valid value for {key}.");

    private static EdgeResilienceOptions ValidateResilienceConfiguration(IConfiguration configuration)
    {
        var settings = configuration.GetSection(EdgeResilienceOptions.SectionName).Get<EdgeResilienceOptions>()
                       ?? new EdgeResilienceOptions();
        if (settings.HttpMaxRetryAttempts is < 1 or > 5 ||
            settings.HttpRetryBaseDelay < TimeSpan.FromMilliseconds(1) ||
            settings.HttpRetryBaseDelay > TimeSpan.FromSeconds(5) ||
            settings.HttpAttemptTimeout < TimeSpan.FromMilliseconds(100) ||
            settings.HttpAttemptTimeout > TimeSpan.FromSeconds(30) ||
            settings.HttpCircuitFailureRatio is < 0.1 or > 1 ||
            settings.HttpCircuitMinimumThroughput is < 2 or > 1_000 ||
            settings.HttpCircuitSamplingDuration < settings.HttpAttemptTimeout * 2 ||
            settings.HttpCircuitSamplingDuration > TimeSpan.FromMinutes(10) ||
            settings.HttpCircuitBreakDuration < TimeSpan.FromMilliseconds(100) ||
            settings.HttpCircuitBreakDuration > TimeSpan.FromMinutes(5) ||
            settings.SqlMaxRetryAttempts is < 0 or > 5 ||
            settings.SqlRetryBaseDelay < TimeSpan.FromMilliseconds(1) ||
            settings.SqlRetryBaseDelay > TimeSpan.FromSeconds(5) ||
            settings.SqlCircuitFailureThreshold is < 2 or > 100 ||
            settings.SqlCircuitBreakDuration < TimeSpan.FromMilliseconds(100) ||
            settings.SqlCircuitBreakDuration > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Edge HTTP/SQL resilience policy is invalid.");
        }

        return settings;
    }

    private static void ValidateLocalAdministratorConfiguration(IConfiguration configuration)
    {
        var settings = configuration.GetSection(EdgeLocalAdministratorOptions.SectionName)
                           .Get<EdgeLocalAdministratorOptions>()
                       ?? new EdgeLocalAdministratorOptions();
        if (string.IsNullOrWhiteSpace(settings.InitialUserName) || settings.InitialUserName.Length > 128 ||
            settings.SessionLifetime < TimeSpan.FromMinutes(1) || settings.SessionLifetime > TimeSpan.FromHours(24) ||
            settings.LoginPermitLimit is < 1 or > 100 ||
            settings.LoginRateWindow < TimeSpan.FromSeconds(10) ||
            settings.LoginRateWindow > TimeSpan.FromHours(1) ||
            settings.MaximumRequestBodyBytes is < 512 or > 65_536 ||
            settings.InitialPassword is { Length: < 12 or > 512 })
        {
            throw new InvalidOperationException("Edge local-administrator security configuration is invalid.");
        }
    }

    private static void ValidateWorkerConfiguration(IConfiguration configuration)
    {
        var storage = configuration.GetSection(EdgeStorageOptions.SectionName).Get<EdgeStorageOptions>()
                      ?? new EdgeStorageOptions();
        if (storage.MinimumAvailableDiskBytes < 0)
            throw new InvalidOperationException("Edge minimum available-disk threshold cannot be negative.");

        var workers = configuration.GetSection(EdgeWorkerOptions.SectionName).Get<EdgeWorkerOptions>()
                      ?? new EdgeWorkerOptions();
        if (workers.BatchSize <= 0 || workers.MaxOutboxAttempts <= 0 ||
            workers.PollInterval < TimeSpan.FromMilliseconds(100) ||
            workers.ConfigurationSyncInterval < TimeSpan.FromSeconds(5) ||
            workers.HeartbeatInterval < TimeSpan.FromSeconds(5) ||
            workers.CleanupInterval < TimeSpan.FromMinutes(1) ||
            workers.RetentionPeriod < TimeSpan.FromDays(1) ||
            workers.IdempotencyRetentionPeriod < workers.RetentionPeriod)
        {
            throw new InvalidOperationException("Edge worker intervals, limits, or retention settings are invalid.");
        }

        var dispatch = configuration.GetSection(EdgeAutoDispatchOptions.SectionName).Get<EdgeAutoDispatchOptions>()
                       ?? new EdgeAutoDispatchOptions();
        if (dispatch.ScanInterval < TimeSpan.FromSeconds(5) ||
            dispatch.MinimumBagCount <= 0 ||
            dispatch.MaximumBagAge < TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Edge automatic-dispatch policy is invalid.");
    }

    private static void ValidateStorageConfiguration(
        EdgeStorageOptions storage,
        IHostEnvironment? environment)
    {
        Microsoft.Data.Sqlite.SqliteConnectionStringBuilder connection;
        try
        {
            connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(storage.ConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Edge storage connection configuration is invalid.");
        }

        if (string.IsNullOrWhiteSpace(connection.DataSource) ||
            !string.IsNullOrEmpty(connection.Password) ||
            connection.Cache == Microsoft.Data.Sqlite.SqliteCacheMode.Shared ||
            storage.MinimumAvailableDiskBytes < 0 ||
            string.IsNullOrWhiteSpace(storage.BackupDirectory) ||
            storage.BackupDirectory.Any(char.IsControl))
        {
            throw new InvalidOperationException("Edge storage security configuration is invalid.");
        }

        if (environment?.IsProduction() == true && !storage.EncryptionEnabled)
            throw new InvalidOperationException("Production Edge storage requires encryption at rest.");
        if (!storage.EncryptionEnabled)
        {
            if (!string.IsNullOrWhiteSpace(storage.RestoreFromBackupPath))
                throw new InvalidOperationException("Restoring an Edge backup requires storage encryption.");
            return;
        }

        _ = EdgeStorageConnection.ResolveDatabasePath(storage);
        _ = new EnvironmentEdgeStorageKeyProvider(
            Microsoft.Extensions.Options.Options.Create(storage)).GetRequiredKeys();
    }
}
