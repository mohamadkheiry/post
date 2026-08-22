using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Post.Core.Application.Abstractions;
using Post.Core.Caching;
using Post.Core.Infrastructure.Hosting;
using Post.Core.Infrastructure.Messaging;
using Post.Core.Infrastructure.ObjectStorage;
using Post.Core.Infrastructure.Persistence;
using Post.Core.Infrastructure.Security;

namespace Post.Core.Infrastructure;

public enum CoreDatabaseProvider
{
    SqlServer = 0,
    Sqlite = 1
}

public enum CoreInfrastructureEnvironment
{
    Production = 0,
    Testing = 1,
    Development = 2
}

public enum CoreServiceProfile
{
    All = 0,
    ParcelLifecycle = 1,
    DeviceConfig = 2,
    DataIngestion = 3,
    ObjectStorage = 4,
    EdgeHealth = 5,
    OperationalResult = 6,
    Identity = 7
}

public sealed record CoreInfrastructureOptions
{
    public CoreInfrastructureEnvironment Environment { get; init; } = CoreInfrastructureEnvironment.Production;

    public CoreServiceProfile ServiceProfile { get; init; } = CoreServiceProfile.All;

    public CoreDatabaseProvider DatabaseProvider { get; init; } = CoreDatabaseProvider.SqlServer;

    public string ParcelLifecycleConnectionStringName { get; init; } = "ParcelLifecycle";

    public string DeviceConfigConnectionStringName { get; init; } = "DeviceConfig";

    public string EdgeHealthConnectionStringName { get; init; } = "EdgeHealth";

    public string OperationalResultConnectionStringName { get; init; } = "OperationalResult";

    public string IdentityConnectionStringName { get; init; } = "Identity";

    public string? ParcelLifecycleConnectionString { get; init; }

    public string? DeviceConfigConnectionString { get; init; }

    public string? EdgeHealthConnectionString { get; init; }

    public string? OperationalResultConnectionString { get; init; }

    public string? IdentityConnectionString { get; init; }

    public bool EnableOutboxProcessing { get; init; }

    public TimeSpan OutboxPollingInterval { get; init; } = TimeSpan.FromSeconds(2);

    public int OutboxBatchSize { get; init; } = 100;

    public int OutboxMaximumAttempts { get; init; } = 10;

    public bool EnableParcelEventConsumer { get; init; } = true;

    public bool EnableAttachmentMetadataConsumer { get; init; } = true;

    public bool EnablePendingAttachmentReconciliation { get; init; } = true;

    public bool EnableEdgeHealthMonitoring { get; init; } = true;

    public static CoreInfrastructureOptions CreateTesting(
        string databaseDirectory,
        CoreServiceProfile serviceProfile = CoreServiceProfile.All)
    {
        if (string.IsNullOrWhiteSpace(databaseDirectory))
        {
            throw new ArgumentException("A testing database directory is required.", nameof(databaseDirectory));
        }

        var fullPath = Path.GetFullPath(databaseDirectory);
        Directory.CreateDirectory(fullPath);
        return new CoreInfrastructureOptions
        {
            Environment = CoreInfrastructureEnvironment.Testing,
            ServiceProfile = serviceProfile,
            DatabaseProvider = CoreDatabaseProvider.Sqlite,
            ParcelLifecycleConnectionString = SqliteFile(fullPath, "parcel-lifecycle.db"),
            DeviceConfigConnectionString = SqliteFile(fullPath, "device-config.db"),
            EdgeHealthConnectionString = SqliteFile(fullPath, "edge-health.db"),
            OperationalResultConnectionString = SqliteFile(fullPath, "operational-result.db"),
            IdentityConnectionString = SqliteFile(fullPath, "identity.db")
        };
    }

    private static string SqliteFile(string directory, string name) =>
        string.Concat("Data Source=", Path.Combine(directory, name));
}

public static class DependencyInjection
{
    public static IServiceCollection AddCoreInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        CoreInfrastructureOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        options ??= new CoreInfrastructureOptions();
        ValidateOptions(options);

        services.TryAddSingleton(options);
        AddIdempotencyCaching(services, configuration, options);
        AddSelectedPersistence(services, configuration, options);
        AddSelectedAdapters(services, configuration, options);
        if (HasDatabase(options.ServiceProfile))
        {
            services.TryAddScoped<ICoreDatabaseInitializer>(provider =>
                new CoreDatabaseInitializer(provider, options.ServiceProfile, options.Environment));
        }

        if (options.EnableOutboxProcessing)
        {
            services.AddHostedService<CoreOutboxBackgroundService>();
        }

        if (options.Environment != CoreInfrastructureEnvironment.Testing &&
            options.ServiceProfile is CoreServiceProfile.All or CoreServiceProfile.ParcelLifecycle)
        {
            if (options.EnableParcelEventConsumer)
            {
                services.TryAddSingleton(ReadParcelConsumerOptions(configuration));
                services.TryAddScoped<IParcelEventDeliveryProcessor, ParcelEventDeliveryProcessor>();
                services.AddHostedService<CoreParcelEventConsumerBackgroundService>();
            }

            if (options.EnableAttachmentMetadataConsumer)
            {
                services.TryAddSingleton(ReadAttachmentMetadataConsumerOptions(configuration));
                services.TryAddScoped<IAttachmentMetadataDeliveryProcessor, AttachmentMetadataDeliveryProcessor>();
                services.AddHostedService<CoreAttachmentMetadataConsumerBackgroundService>();
            }

            if (options.EnablePendingAttachmentReconciliation)
            {
                services.TryAddSingleton(ReadPendingAttachmentReconciliationOptions(configuration));
                services.TryAddScoped<
                    IPendingAttachmentReconciliationProcessor,
                    PendingAttachmentReconciliationProcessor>();
                services.AddHostedService<CorePendingAttachmentReconciliationBackgroundService>();
            }
        }

        if (options.Environment != CoreInfrastructureEnvironment.Testing &&
            options.EnableEdgeHealthMonitoring &&
            options.ServiceProfile is CoreServiceProfile.All or CoreServiceProfile.EdgeHealth)
        {
            services.TryAddSingleton(ReadEdgeHealthMonitorOptions(configuration));
            services.AddHostedService<CoreEdgeHealthMonitorBackgroundService>();
        }

        return services;
    }

    private static void AddSelectedPersistence(
        IServiceCollection services,
        IConfiguration configuration,
        CoreInfrastructureOptions options)
    {
        if (options.ServiceProfile is CoreServiceProfile.All or CoreServiceProfile.ParcelLifecycle)
        {
            var connection = ResolveConnection(
                configuration,
                options.ParcelLifecycleConnectionString,
                options.ParcelLifecycleConnectionStringName);
            ValidateDatabaseConnection(options, connection);
            services.AddDbContext<ParcelLifecycleDbContext>(builder =>
                ConfigureDatabase(builder, options.DatabaseProvider, connection));
            services.TryAddScoped<EfParcelLifecycleStore>();
            services.TryAddScoped<IParcelLifecycleStore>(provider =>
                new RedisMirroringParcelLifecycleStore(
                    provider.GetRequiredService<EfParcelLifecycleStore>(),
                    provider.GetRequiredService<SqlAuthoritativeIdempotencyCoordinator>()));
        }

        if (options.ServiceProfile is CoreServiceProfile.All or CoreServiceProfile.DeviceConfig)
        {
            var connection = ResolveConnection(
                configuration,
                options.DeviceConfigConnectionString,
                options.DeviceConfigConnectionStringName);
            ValidateDatabaseConnection(options, connection);
            services.AddDbContext<DeviceConfigDbContext>(builder =>
                ConfigureDatabase(builder, options.DatabaseProvider, connection));
            services.TryAddScoped<IDeviceConfigStore, EfDeviceConfigStore>();
        }

        if (options.ServiceProfile is CoreServiceProfile.All or CoreServiceProfile.EdgeHealth)
        {
            var connection = ResolveConnection(
                configuration,
                options.EdgeHealthConnectionString,
                options.EdgeHealthConnectionStringName);
            ValidateDatabaseConnection(options, connection);
            services.AddDbContext<EdgeHealthDbContext>(builder =>
                ConfigureDatabase(builder, options.DatabaseProvider, connection));
            services.TryAddScoped<IEdgeHealthStore, EfEdgeHealthStore>();
        }

        if (options.ServiceProfile is CoreServiceProfile.All or CoreServiceProfile.OperationalResult)
        {
            var connection = ResolveConnection(
                configuration,
                options.OperationalResultConnectionString,
                options.OperationalResultConnectionStringName);
            ValidateDatabaseConnection(options, connection);
            services.AddDbContext<OperationalResultDbContext>(builder =>
                ConfigureDatabase(builder, options.DatabaseProvider, connection));
            services.TryAddScoped<EfOperationalResultStore>();
            services.TryAddScoped<IOperationalResultStore>(provider =>
                new RedisMirroringOperationalResultStore(
                    provider.GetRequiredService<EfOperationalResultStore>(),
                    provider.GetRequiredService<OperationalResultDbContext>(),
                    provider.GetRequiredService<SqlAuthoritativeIdempotencyCoordinator>()));
        }

        if (options.ServiceProfile is CoreServiceProfile.All or CoreServiceProfile.Identity)
        {
            var connection = ResolveConnection(
                configuration,
                options.IdentityConnectionString,
                options.IdentityConnectionStringName);
            ValidateDatabaseConnection(options, connection);
            services.AddDbContext<IdentityDbContext>(builder =>
                ConfigureDatabase(builder, options.DatabaseProvider, connection));
            services.AddIdentityCore<AppUser>(identity =>
                {
                    identity.Password.RequiredLength = 12;
                    identity.Password.RequiredUniqueChars = 1;
                    identity.Password.RequireDigit = false;
                    identity.Password.RequireLowercase = false;
                    identity.Password.RequireNonAlphanumeric = false;
                    identity.Password.RequireUppercase = false;
                    identity.User.RequireUniqueEmail = false;
                })
                .AddRoles<AppRole>()
                .AddEntityFrameworkStores<IdentityDbContext>();
            services.Configure<PasswordHasherOptions>(hasher =>
            {
                hasher.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
                hasher.IterationCount = 210_000;
            });
            services.TryAddScoped<IIdentityStore, EfIdentityStore>();
            services.TryAddScoped<AspNetAdminIdentityAuthority>();
            services.TryAddScoped<IAdminIdentityAuthority>(provider =>
                provider.GetRequiredService<AspNetAdminIdentityAuthority>());
        }

        AddOutboxStore(services, options.ServiceProfile);
    }

    private static void AddIdempotencyCaching(
        IServiceCollection services,
        IConfiguration configuration,
        CoreInfrastructureOptions options)
    {
        if (options.ServiceProfile is not CoreServiceProfile.All and
            not CoreServiceProfile.ParcelLifecycle and
            not CoreServiceProfile.OperationalResult)
        {
            return;
        }

        var enabled = options.Environment != CoreInfrastructureEnvironment.Testing &&
                      configuration.GetValue("Redis:Enabled", false);
        if (options.Environment == CoreInfrastructureEnvironment.Production && !enabled)
        {
            throw new InvalidOperationException(
                "Production ParcelLifecycle and OperationalResult services require Redis:Enabled=true.");
        }

        var configuredTls = configuration["Redis:RequireTls"];
        if (enabled &&
            options.Environment == CoreInfrastructureEnvironment.Development &&
            string.IsNullOrWhiteSpace(configuredTls))
        {
            throw new InvalidOperationException(
                "Development Redis requires an explicit Redis:RequireTls setting.");
        }

        services.AddRedisIdempotencyCache(new RedisIdempotencyCacheOptions
        {
            Enabled = enabled,
            ConnectionString = configuration["Redis:ConnectionString"],
            InstancePrefix = configuration["Redis:InstancePrefix"] ?? "post:core",
            EntryTtl = configuration.GetValue("Redis:EntryTtl", TimeSpan.FromHours(24)),
            CleanupInterval = configuration.GetValue("Redis:CleanupInterval", TimeSpan.FromMinutes(5)),
            CleanupBatchSize = configuration.GetValue("Redis:CleanupBatchSize", 500),
            RequireTls = options.Environment == CoreInfrastructureEnvironment.Production ||
                         configuration.GetValue("Redis:RequireTls", false),
            RequireAuthentication = options.Environment == CoreInfrastructureEnvironment.Production
        });
    }

    private static void AddOutboxStore(IServiceCollection services, CoreServiceProfile profile)
    {
        switch (profile)
        {
            case CoreServiceProfile.All:
                services.TryAddScoped<IOutboxStore, CompositeEfOutboxStore>();
                break;
            case CoreServiceProfile.ParcelLifecycle:
                services.TryAddScoped<IOutboxStore>(provider =>
                {
                    var context = provider.GetRequiredService<ParcelLifecycleDbContext>();
                    return new EfSingleDatabaseOutboxStore(context, context.OutboxMessages);
                });
                break;
            case CoreServiceProfile.DeviceConfig:
                services.TryAddScoped<IOutboxStore>(provider =>
                {
                    var context = provider.GetRequiredService<DeviceConfigDbContext>();
                    return new EfSingleDatabaseOutboxStore(context, context.OutboxMessages);
                });
                break;
            case CoreServiceProfile.EdgeHealth:
                services.TryAddScoped<IOutboxStore>(provider =>
                {
                    var context = provider.GetRequiredService<EdgeHealthDbContext>();
                    return new EfSingleDatabaseOutboxStore(context, context.OutboxMessages);
                });
                break;
            case CoreServiceProfile.OperationalResult:
                services.TryAddScoped<IOutboxStore>(provider =>
                {
                    var context = provider.GetRequiredService<OperationalResultDbContext>();
                    return new EfSingleDatabaseOutboxStore(context, context.OutboxMessages);
                });
                break;
            case CoreServiceProfile.Identity:
                services.TryAddScoped<IOutboxStore>(provider =>
                {
                    var context = provider.GetRequiredService<IdentityDbContext>();
                    return new EfSingleDatabaseOutboxStore(context, context.OutboxMessages);
                });
                break;
            case CoreServiceProfile.DataIngestion:
            case CoreServiceProfile.ObjectStorage:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unsupported Core service profile.");
        }
    }

    private static void AddSelectedAdapters(
        IServiceCollection services,
        IConfiguration configuration,
        CoreInfrastructureOptions options)
    {
        var profile = options.ServiceProfile;
        if (profile is CoreServiceProfile.All or CoreServiceProfile.DeviceConfig or CoreServiceProfile.Identity)
        {
            services.TryAddSingleton<ISecretHasher, Pbkdf2SecretHasher>();
        }


        if (profile is CoreServiceProfile.All or CoreServiceProfile.Identity)
        {
            var jwtOptions = ReadJwtOptions(configuration, options.Environment);
            services.TryAddSingleton(jwtOptions);
            services.TryAddSingleton<ITokenIssuer, HmacJwtTokenIssuer>();
        }

        if (profile is CoreServiceProfile.All or CoreServiceProfile.ObjectStorage)
        {
            var s3Options = ReadS3Options(configuration, options.Environment);
            services.TryAddSingleton(s3Options);
            services.TryAddSingleton<IS3Presigner, S3SignatureV4Presigner>();
        }

        if (profile is not CoreServiceProfile.ObjectStorage)
        {
            AddDurablePublisher(services, configuration, options.Environment);
        }
    }

    private static void AddDurablePublisher(
        IServiceCollection services,
        IConfiguration configuration,
        CoreInfrastructureEnvironment environment)
    {
        if (environment == CoreInfrastructureEnvironment.Testing)
        {
            services.TryAddSingleton<InMemoryDurableEventPublisher>();
            services.TryAddSingleton<IDurableEventPublisher>(provider =>
                provider.GetRequiredService<InMemoryDurableEventPublisher>());
        }
        else
        {
            services.TryAddSingleton(ReadKafkaOptions(configuration, environment));
            services.TryAddSingleton<IKafkaCompatibleProducer, ConfluentKafkaProducer>();
            services.TryAddSingleton<IDurableEventPublisher, KafkaDurableEventPublisher>();
        }
    }

    private static void ConfigureDatabase(
        DbContextOptionsBuilder builder,
        CoreDatabaseProvider provider,
        string connectionString)
    {
        switch (provider)
        {
            case CoreDatabaseProvider.SqlServer:
                builder.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
                break;
            case CoreDatabaseProvider.Sqlite:
                builder.UseSqlite(connectionString);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported Core database provider.");
        }
    }

    private static void ValidateDatabaseConnection(CoreInfrastructureOptions options, string connectionString)
    {
        if (options.Environment == CoreInfrastructureEnvironment.Production &&
            options.DatabaseProvider == CoreDatabaseProvider.SqlServer)
        {
            ValidateProductionSqlConnection(connectionString);
        }
    }

    private static void ValidateProductionSqlConnection(string connectionString)
    {
        SqlConnectionStringBuilder sqlConnection;
        try
        {
            sqlConnection = new SqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Production SQL Server connection string is invalid.", exception);
        }

        if (string.IsNullOrWhiteSpace(sqlConnection.DataSource) ||
            !sqlConnection.Encrypt ||
            sqlConnection.TrustServerCertificate ||
            sqlConnection.ConnectTimeout is < 1 or > 60)
        {
            throw new InvalidOperationException(
                "Production SQL Server requires encrypted transport, certificate validation, and a 1-60 second connect timeout.");
        }
    }

    private static HmacJwtOptions ReadJwtOptions(
        IConfiguration configuration,
        CoreInfrastructureEnvironment environment)
    {
        var issuer = configuration["Auth:Issuer"] ?? configuration["Core:Jwt:Issuer"];
        var audience = configuration["Auth:Audience"] ?? configuration["Core:Jwt:Audience"];
        var signingKey = configuration["Auth:SigningKey"] ?? configuration["Core:Jwt:SigningKey"];
        if (environment == CoreInfrastructureEnvironment.Testing)
        {
            issuer ??= "post-core-tests";
            audience ??= "post-core-tests";
            signingKey ??= "testing-only-signing-key-32-bytes-minimum";
        }

        return new HmacJwtOptions
        {
            Issuer = Required(issuer, "Auth:Issuer"),
            Audience = Required(audience, "Auth:Audience"),
            SigningKey = Required(signingKey, "Auth:SigningKey")
        };
    }

    private static KafkaTransportOptions ReadKafkaOptions(
        IConfiguration configuration,
        CoreInfrastructureEnvironment environment)
    {
        var bootstrapServers = configuration["Messaging:Kafka:BootstrapServers"] ??
            configuration["Core:Kafka:BootstrapServers"];
        var protocolValue = configuration["Messaging:Kafka:SecurityProtocol"] ??
            configuration["Core:Kafka:SecurityProtocol"] ??
            (environment == CoreInfrastructureEnvironment.Production
                ? nameof(Confluent.Kafka.SecurityProtocol.Ssl)
                : nameof(Confluent.Kafka.SecurityProtocol.Plaintext));
        if (!Enum.TryParse<Confluent.Kafka.SecurityProtocol>(protocolValue, true, out var securityProtocol))
        {
            throw new InvalidOperationException($"Kafka security protocol '{protocolValue}' is invalid.");
        }

        if (environment == CoreInfrastructureEnvironment.Production &&
            securityProtocol is not Confluent.Kafka.SecurityProtocol.Ssl and
            not Confluent.Kafka.SecurityProtocol.SaslSsl)
        {
            throw new InvalidOperationException(
                "Production Kafka transport requires SecurityProtocol=Ssl or SaslSsl.");
        }

        var mechanismValue = configuration["Messaging:Kafka:SaslMechanism"] ??
            configuration["Core:Kafka:SaslMechanism"];
        Confluent.Kafka.SaslMechanism? saslMechanism = null;
        if (!string.IsNullOrWhiteSpace(mechanismValue))
        {
            if (!Enum.TryParse<Confluent.Kafka.SaslMechanism>(mechanismValue, true, out var parsedMechanism))
            {
                throw new InvalidOperationException($"Kafka SASL mechanism '{mechanismValue}' is invalid.");
            }

            saslMechanism = parsedMechanism;
        }

        return new KafkaTransportOptions
        {
            BootstrapServers = Required(bootstrapServers, "Messaging:Kafka:BootstrapServers"),
            ClientId = configuration["Messaging:Kafka:ClientId"] ??
                configuration["Core:Kafka:ClientId"] ?? "post-core",
            SecurityProtocol = securityProtocol,
            SaslMechanism = saslMechanism,
            SaslUsername = configuration["Messaging:Kafka:SaslUsername"] ??
                configuration["Core:Kafka:SaslUsername"],
            SaslPassword = configuration["Messaging:Kafka:SaslPassword"] ??
                configuration["Core:Kafka:SaslPassword"],
            SslCaLocation = configuration["Messaging:Kafka:SslCaLocation"] ??
                configuration["Core:Kafka:SslCaLocation"],
            MaximumPayloadBytes = ReadKafkaMaximumPayloadBytes(configuration),
            DeliveryTimeoutMilliseconds = ReadBoundedInteger(
                configuration,
                "Messaging:Kafka:DeliveryTimeoutMilliseconds",
                30_000,
                1_000,
                120_000),
            SocketTimeoutMilliseconds = ReadBoundedInteger(
                configuration,
                "Messaging:Kafka:SocketTimeoutMilliseconds",
                10_000,
                1_000,
                60_000),
            MaximumSendRetries = ReadBoundedInteger(
                configuration,
                "Messaging:Kafka:MaximumSendRetries",
                5,
                0,
                20)
        };
    }

    private static ParcelEventConsumerOptions ReadParcelConsumerOptions(IConfiguration configuration)
    {
        var retrySeconds = ReadPositiveInteger(
            configuration,
            "Messaging:Kafka:ConsumerFailureRetrySeconds",
            2);
        var maximumAttempts = ReadPositiveInteger(
            configuration,
            "Messaging:Kafka:ConsumerMaximumDeliveryAttempts",
            5);

        return new ParcelEventConsumerOptions
        {
            Topic = Required(
                configuration["Messaging:Kafka:ParcelEventsTopic"] ??
                configuration["Messaging:Topics:ParcelEvents"] ??
                "parcel.events.received",
                "Messaging:Kafka:ParcelEventsTopic"),
            DeadLetterTopic = Required(
                configuration["Messaging:Kafka:ParcelEventsDeadLetterTopic"] ?? "parcel.events.received.dlq",
                "Messaging:Kafka:ParcelEventsDeadLetterTopic"),
            GroupId = Required(
                configuration["Messaging:Kafka:ParcelLifecycleGroupId"] ?? "post-core-parcel-lifecycle",
                "Messaging:Kafka:ParcelLifecycleGroupId"),
            FailureRetryDelay = TimeSpan.FromSeconds(retrySeconds),
            MaximumDeliveryAttempts = maximumAttempts,
            MaximumPayloadBytes = ReadKafkaMaximumPayloadBytes(configuration)
        };
    }

    private static AttachmentMetadataConsumerOptions ReadAttachmentMetadataConsumerOptions(
        IConfiguration configuration)
    {
        var retrySeconds = ReadPositiveInteger(
            configuration,
            "Messaging:Kafka:ConsumerFailureRetrySeconds",
            2);
        var maximumAttempts = ReadPositiveInteger(
            configuration,
            "Messaging:Kafka:ConsumerMaximumDeliveryAttempts",
            5);

        return new AttachmentMetadataConsumerOptions
        {
            Topic = Required(
                configuration["Messaging:Kafka:AttachmentMetadataTopic"] ??
                configuration["Messaging:Topics:ImageMetadata"] ??
                "image.metadata.received",
                "Messaging:Kafka:AttachmentMetadataTopic"),
            DeadLetterTopic = Required(
                configuration["Messaging:Kafka:AttachmentMetadataDeadLetterTopic"] ??
                "image.metadata.received.dlq",
                "Messaging:Kafka:AttachmentMetadataDeadLetterTopic"),
            GroupId = Required(
                configuration["Messaging:Kafka:AttachmentMetadataGroupId"] ??
                "post-core-attachment-linking",
                "Messaging:Kafka:AttachmentMetadataGroupId"),
            FailureRetryDelay = TimeSpan.FromSeconds(retrySeconds),
            MaximumDeliveryAttempts = maximumAttempts,
            MaximumPayloadBytes = ReadKafkaMaximumPayloadBytes(configuration)
        };
    }

    private static int ReadKafkaMaximumPayloadBytes(IConfiguration configuration)
    {
        var value = configuration.GetValue(
            "Messaging:Kafka:MaximumPayloadBytes",
            KafkaTransportOptions.MaximumSupportedPayloadBytes);
        if (value is < KafkaTransportOptions.MinimumPayloadBytes or
            > KafkaTransportOptions.MaximumSupportedPayloadBytes)
        {
            throw new InvalidOperationException(
                $"Messaging:Kafka:MaximumPayloadBytes must be between " +
                $"{KafkaTransportOptions.MinimumPayloadBytes} and " +
                $"{KafkaTransportOptions.MaximumSupportedPayloadBytes}.");
        }

        return value;
    }

    private static int ReadBoundedInteger(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var value = configuration.GetValue(key, defaultValue);
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException($"{key} must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static EdgeHealthMonitorOptions ReadEdgeHealthMonitorOptions(IConfiguration configuration) =>
        new()
        {
            CheckInterval = TimeSpan.FromSeconds(ReadPositiveInteger(
                configuration,
                "Core:EdgeHealthMonitor:CheckIntervalSeconds",
                30)),
            StaleThreshold = TimeSpan.FromSeconds(ReadPositiveInteger(
                configuration,
                "Core:EdgeHealthMonitor:StaleThresholdSeconds",
                300))
        };

    private static PendingAttachmentReconciliationOptions ReadPendingAttachmentReconciliationOptions(
        IConfiguration configuration)
    {
        var baseRetrySeconds = ReadPositiveInteger(
            configuration,
            "Core:PendingAttachments:BaseRetrySeconds",
            5);
        var maximumRetrySeconds = ReadPositiveInteger(
            configuration,
            "Core:PendingAttachments:MaximumRetrySeconds",
            300);
        if (maximumRetrySeconds < baseRetrySeconds)
        {
            throw new InvalidOperationException(
                "Core:PendingAttachments:MaximumRetrySeconds cannot be less than BaseRetrySeconds.");
        }

        return new PendingAttachmentReconciliationOptions
        {
            PollingInterval = TimeSpan.FromSeconds(ReadPositiveInteger(
                configuration,
                "Core:PendingAttachments:PollingSeconds",
                30)),
            BaseRetryDelay = TimeSpan.FromSeconds(baseRetrySeconds),
            MaximumRetryDelay = TimeSpan.FromSeconds(maximumRetrySeconds),
            MaximumAttempts = ReadPositiveInteger(
                configuration,
                "Core:PendingAttachments:MaximumAttempts",
                10),
            MaximumAge = TimeSpan.FromHours(ReadPositiveInteger(
                configuration,
                "Core:PendingAttachments:MaximumAgeHours",
                24)),
            BatchSize = ReadPositiveInteger(
                configuration,
                "Core:PendingAttachments:BatchSize",
                50),
            DeadLetterTopic = Required(
                configuration["Core:PendingAttachments:DeadLetterTopic"] ?? "image.metadata.pending.dlq",
                "Core:PendingAttachments:DeadLetterTopic")
        };
    }

    private static int ReadPositiveInteger(
        IConfiguration configuration,
        string key,
        int defaultValue)
    {
        var value = int.TryParse(
            configuration[key],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var configuredValue)
            ? configuredValue
            : defaultValue;
        return value > 0
            ? value
            : throw new InvalidOperationException($"Required configuration '{key}' must be positive.");
    }

    private static S3PresignerOptions ReadS3Options(
        IConfiguration configuration,
        CoreInfrastructureEnvironment environment)
    {
        var endpoint = configuration["Core:S3:Endpoint"];
        var region = configuration["Core:S3:Region"];
        var bucket = configuration["Core:S3:Bucket"];
        var accessKey = configuration["Core:S3:AccessKey"];
        var secretKey = configuration["Core:S3:SecretKey"];
        if (environment == CoreInfrastructureEnvironment.Testing)
        {
            endpoint ??= "http://localhost:9000";
            region ??= "us-east-1";
            bucket ??= "post-core-tests";
            accessKey ??= "testing-access-key";
            secretKey ??= "testing-secret-key";
        }

        return new S3PresignerOptions
        {
            Endpoint = new Uri(Required(endpoint, "Core:S3:Endpoint"), UriKind.Absolute),
            Region = Required(region, "Core:S3:Region"),
            Bucket = Required(bucket, "Core:S3:Bucket"),
            AccessKey = Required(accessKey, "Core:S3:AccessKey"),
            SecretKey = Required(secretKey, "Core:S3:SecretKey"),
            RequireHttps = environment == CoreInfrastructureEnvironment.Production,
            RequireServerSideEncryption = environment == CoreInfrastructureEnvironment.Production ||
                configuration.GetValue("Core:S3:RequireServerSideEncryption", false),
            ForcePathStyle = !bool.TryParse(configuration["Core:S3:ForcePathStyle"], out var forcePathStyle) ||
                forcePathStyle
        };
    }

    private static string ResolveConnection(
        IConfiguration configuration,
        string? explicitValue,
        string name)
    {
        var value = explicitValue ?? configuration.GetConnectionString(name);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required connection string '{name}' is missing.")
            : value;
    }

    private static string Required(string? value, string configurationKey) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required configuration '{configurationKey}' is missing.")
            : value;

    private static bool HasDatabase(CoreServiceProfile profile) =>
        profile is not CoreServiceProfile.DataIngestion and not CoreServiceProfile.ObjectStorage;

    private static void ValidateOptions(CoreInfrastructureOptions options)
    {
        if (options.EnableOutboxProcessing && !HasDatabase(options.ServiceProfile))
        {
            throw new InvalidOperationException("Outbox processing requires a stateful Core service profile.");
        }

        if (options.EnableOutboxProcessing &&
            (options.OutboxPollingInterval <= TimeSpan.Zero || options.OutboxBatchSize <= 0 ||
             options.OutboxMaximumAttempts <= 0))
        {
            throw new InvalidOperationException("Outbox polling interval, batch size, and attempts must be positive.");
        }
    }
}

public interface ICoreDatabaseInitializer
{
    Task InitializeAsync(bool applyMigrations, CancellationToken cancellationToken = default);
}

internal sealed class CoreDatabaseInitializer(
    IServiceProvider serviceProvider,
    CoreServiceProfile profile,
    CoreInfrastructureEnvironment environment) : ICoreDatabaseInitializer
{
    public async Task InitializeAsync(bool applyMigrations, CancellationToken cancellationToken = default)
    {
        if (environment == CoreInfrastructureEnvironment.Production && !applyMigrations)
        {
            throw new InvalidOperationException(
                "Production Core databases must be initialized with EF migrations; EnsureCreated is testing-only.");
        }

        foreach (var database in GetDatabases())
        {
            if (applyMigrations)
            {
                await database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private List<Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade> GetDatabases()
    {
        var databases = new List<Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade>();
        if (profile is CoreServiceProfile.All or CoreServiceProfile.ParcelLifecycle)
        {
            databases.Add(serviceProvider.GetRequiredService<ParcelLifecycleDbContext>().Database);
        }

        if (profile is CoreServiceProfile.All or CoreServiceProfile.DeviceConfig)
        {
            databases.Add(serviceProvider.GetRequiredService<DeviceConfigDbContext>().Database);
        }

        if (profile is CoreServiceProfile.All or CoreServiceProfile.EdgeHealth)
        {
            databases.Add(serviceProvider.GetRequiredService<EdgeHealthDbContext>().Database);
        }

        if (profile is CoreServiceProfile.All or CoreServiceProfile.OperationalResult)
        {
            databases.Add(serviceProvider.GetRequiredService<OperationalResultDbContext>().Database);
        }

        if (profile is CoreServiceProfile.All or CoreServiceProfile.Identity)
        {
            databases.Add(serviceProvider.GetRequiredService<IdentityDbContext>().Database);
        }

        return databases;
    }
}
