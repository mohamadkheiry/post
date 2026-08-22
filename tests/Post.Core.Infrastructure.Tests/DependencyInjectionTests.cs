using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Post.Core.Application;
using Post.Core.Application.Abstractions;
using Post.Core.Infrastructure.Hosting;
using Post.Core.Infrastructure.Messaging;
using Post.Core.Infrastructure.Persistence;

namespace Post.Core.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    private static string AuthenticatedRedisConnectionString => string.Join(
        ',',
        "cache.example.test:6380",
        "ssl=true",
        "user=post-core",
        string.Concat("pass", "word=synthetic-test-value"),
        "abortConnect=false");

    [Fact]
    public void TestingOptionsCreateAnIsolatedFiveDatabaseLayoutAndRejectBlankPaths()
    {
        Assert.Throws<ArgumentException>(() => CoreInfrastructureOptions.CreateTesting("   "));
        var directory = Path.Combine(
            Path.GetTempPath(),
            string.Concat("post-core-options-", Guid.NewGuid().ToString("N")));
        try
        {
            var options = CoreInfrastructureOptions.CreateTesting(
                directory,
                CoreServiceProfile.DeviceConfig);

            Assert.Equal(CoreInfrastructureEnvironment.Testing, options.Environment);
            Assert.Equal(CoreServiceProfile.DeviceConfig, options.ServiceProfile);
            Assert.Equal(CoreDatabaseProvider.Sqlite, options.DatabaseProvider);
            Assert.True(Directory.Exists(directory));
            Assert.Equal($"Data Source={Path.Combine(directory, "parcel-lifecycle.db")}", options.ParcelLifecycleConnectionString);
            Assert.Equal($"Data Source={Path.Combine(directory, "device-config.db")}", options.DeviceConfigConnectionString);
            Assert.Equal($"Data Source={Path.Combine(directory, "edge-health.db")}", options.EdgeHealthConnectionString);
            Assert.Equal($"Data Source={Path.Combine(directory, "operational-result.db")}", options.OperationalResultConnectionString);
            Assert.Equal($"Data Source={Path.Combine(directory, "identity.db")}", options.IdentityConnectionString);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    [Fact]
    public void InitialMigrationsGenerateSqlForAllFiveLogicalDatabases()
    {
        using var parcel = new ParcelLifecycleDesignTimeDbContextFactory().CreateDbContext([]);
        using var device = new DeviceConfigDesignTimeDbContextFactory().CreateDbContext([]);
        using var health = new EdgeHealthDesignTimeDbContextFactory().CreateDbContext([]);
        using var operational = new OperationalResultDesignTimeDbContextFactory().CreateDbContext([]);
        using var identity = new IdentityDesignTimeDbContextFactory().CreateDbContext([]);

        foreach (var context in new DbContext[] { parcel, device, health, operational, identity })
        {
            Assert.NotEmpty(context.Database.GetMigrations());
            var script = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().GenerateScript();
            Assert.Contains("CREATE TABLE", script, StringComparison.OrdinalIgnoreCase);
        }

        var edgeHealthScript = health.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().GenerateScript();
        Assert.Contains("IdempotencyRecords", edgeHealthScript, StringComparison.Ordinal);
        var identityScript = identity.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().GenerateScript();
        Assert.Contains("AspNetUsers", identityScript, StringComparison.Ordinal);
        Assert.Contains("AspNetRoles", identityScript, StringComparison.Ordinal);
        Assert.Contains("IdentityBootstrapLocks", identityScript, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO [AspNetUsers]", identityScript, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO [AspNetUserRoles]", identityScript, StringComparison.Ordinal);
        Assert.True(
            identityScript.IndexOf("INSERT INTO [AspNetUsers]", StringComparison.Ordinal) <
            identityScript.IndexOf("DROP TABLE [AppUsers]", StringComparison.Ordinal),
            "The ASP.NET Core Identity migration must copy users before dropping the legacy authority table.");
    }

    [Fact]
    public void DesignTimeFactoriesSelectSqlServerForAllContexts()
    {
        using var parcel = new ParcelLifecycleDesignTimeDbContextFactory().CreateDbContext([]);
        using var device = new DeviceConfigDesignTimeDbContextFactory().CreateDbContext([]);
        using var health = new EdgeHealthDesignTimeDbContextFactory().CreateDbContext([]);
        using var operational = new OperationalResultDesignTimeDbContextFactory().CreateDbContext([]);
        using var identity = new IdentityDesignTimeDbContextFactory().CreateDbContext([]);

        Assert.All(
            new DbContext[] { parcel, device, health, operational, identity },
            context => Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", context.Database.ProviderName));
    }

    [Fact]
    public async Task ProductionDatabaseInitializationRejectsEnsureCreated()
    {
        var configuration = new ConfigurationManager
        {
            ["Messaging:Kafka:BootstrapServers"] = "redpanda:9092",
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] = AuthenticatedRedisConnectionString,
            ["Redis:RequireTls"] = "true"
        };
        var services = new ServiceCollection();
        services.AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
        {
            ServiceProfile = CoreServiceProfile.ParcelLifecycle,
            DatabaseProvider = CoreDatabaseProvider.Sqlite,
            ParcelLifecycleConnectionString = "Data Source=:memory:",
            EnableParcelEventConsumer = false
        });

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<ICoreDatabaseInitializer>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync(applyMigrations: false, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(CoreServiceProfile.All, 5)]
    [InlineData(CoreServiceProfile.ParcelLifecycle, 1)]
    [InlineData(CoreServiceProfile.DeviceConfig, 1)]
    [InlineData(CoreServiceProfile.EdgeHealth, 1)]
    [InlineData(CoreServiceProfile.OperationalResult, 1)]
    [InlineData(CoreServiceProfile.Identity, 1)]
    public async Task TestingDatabaseInitializationCreatesOnlyTheSelectedProfileDatabases(
        CoreServiceProfile profile,
        int expectedDatabaseCount)
    {
        var services = new ServiceCollection();
        services.AddCoreInfrastructure(new ConfigurationManager(), new CoreInfrastructureOptions
        {
            Environment = CoreInfrastructureEnvironment.Testing,
            ServiceProfile = profile,
            DatabaseProvider = CoreDatabaseProvider.Sqlite,
            ParcelLifecycleConnectionString = "Data Source=:memory:",
            DeviceConfigConnectionString = "Data Source=:memory:",
            EdgeHealthConnectionString = "Data Source=:memory:",
            OperationalResultConnectionString = "Data Source=:memory:",
            IdentityConnectionString = "Data Source=:memory:",
            EnableOutboxProcessing = false
        });

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var contexts = new DbContext?[]
        {
            scope.ServiceProvider.GetService<ParcelLifecycleDbContext>(),
            scope.ServiceProvider.GetService<DeviceConfigDbContext>(),
            scope.ServiceProvider.GetService<EdgeHealthDbContext>(),
            scope.ServiceProvider.GetService<OperationalResultDbContext>(),
            scope.ServiceProvider.GetService<IdentityDbContext>()
        }.OfType<DbContext>().ToArray();
        Assert.Equal(expectedDatabaseCount, contexts.Length);
        foreach (var context in contexts)
        {
            await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        }

        var initializer = scope.ServiceProvider.GetRequiredService<ICoreDatabaseInitializer>();
        await initializer.InitializeAsync(applyMigrations: false, cancellationToken: TestContext.Current.CancellationToken);

        foreach (var context in contexts)
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture) > 0);
        }
    }

    [Fact]
    public void StatelessProfilesDoNotRequireOrRegisterDatabases()
    {
        var configuration = new ConfigurationManager();
        var ingestion = new ServiceCollection();
        ingestion.AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
        {
            Environment = CoreInfrastructureEnvironment.Testing,
            ServiceProfile = CoreServiceProfile.DataIngestion
        });
        Assert.DoesNotContain(ingestion, x => x.ServiceType == typeof(ParcelLifecycleDbContext));
        Assert.Contains(ingestion, x => x.ServiceType == typeof(IDurableEventPublisher));

        var storage = new ServiceCollection();
        storage.AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
        {
            Environment = CoreInfrastructureEnvironment.Testing,
            ServiceProfile = CoreServiceProfile.ObjectStorage
        });
        Assert.DoesNotContain(storage, x => x.ServiceType == typeof(ParcelLifecycleDbContext));
        Assert.DoesNotContain(storage, x => x.ServiceType == typeof(IDurableEventPublisher));
        Assert.Contains(storage, x => x.ServiceType == typeof(Post.Core.Application.Abstractions.IS3Presigner));
    }

    [Fact]
    public void ProductionObjectStorageRejectsPlainHttpEndpoint()
    {
        var configuration = new ConfigurationManager
        {
            ["Core:S3:Endpoint"] = "http://storage.internal:9000",
            ["Core:S3:Region"] = "us-east-1",
            ["Core:S3:Bucket"] = "post-images",
            ["Core:S3:AccessKey"] = "access",
            ["Core:S3:SecretKey"] = "secret"
        };
        var services = new ServiceCollection();

        services.AddCoreInfrastructure(
            configuration,
            new CoreInfrastructureOptions
            {
                Environment = CoreInfrastructureEnvironment.Production,
                ServiceProfile = CoreServiceProfile.ObjectStorage
            });
        using var provider = services.BuildServiceProvider();

        Assert.Throws<ArgumentException>(() =>
            provider.GetRequiredService<Post.Core.Application.Abstractions.IS3Presigner>());
    }

    [Fact]
    public void ProductionObjectStorageAlwaysRequiresSignedServerSideEncryption()
    {
        var configuration = new ConfigurationManager
        {
            ["Core:S3:Endpoint"] = "https://storage.example.test",
            ["Core:S3:Region"] = "us-east-1",
            ["Core:S3:Bucket"] = "post-images",
            ["Core:S3:AccessKey"] = "access",
            ["Core:S3:SecretKey"] = "secret",
            ["Core:S3:RequireServerSideEncryption"] = "false"
        };
        var services = new ServiceCollection();
        services.AddCoreInfrastructure(
            configuration,
            new CoreInfrastructureOptions
            {
                Environment = CoreInfrastructureEnvironment.Production,
                ServiceProfile = CoreServiceProfile.ObjectStorage
            });
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<Post.Core.Infrastructure.ObjectStorage.S3PresignerOptions>();

        Assert.True(options.RequireServerSideEncryption);
    }

    [Fact]
    public void StatefulProfileRequiresOnlyItsOwnedConnectionString()
    {
        var configuration = new ConfigurationManager();
        var services = new ServiceCollection();
        services.AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
        {
            Environment = CoreInfrastructureEnvironment.Testing,
            ServiceProfile = CoreServiceProfile.Identity,
            DatabaseProvider = CoreDatabaseProvider.Sqlite,
            IdentityConnectionString = "Data Source=:memory:"
        });

        Assert.Contains(services, x => x.ServiceType == typeof(IdentityDbContext));
        Assert.DoesNotContain(services, x => x.ServiceType == typeof(DeviceConfigDbContext));
        Assert.Contains(services, x => x.ServiceType == typeof(IIdentityStore));
    }

    [Fact]
    public void ProductionDataIngestionFailsFastWithoutKafkaAndRegistersConcreteAdapterWhenConfigured()
    {
        var empty = new ConfigurationManager();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCoreInfrastructure(
            empty,
            new CoreInfrastructureOptions { ServiceProfile = CoreServiceProfile.DataIngestion }));

        var configuration = new ConfigurationManager
        {
            ["Messaging:Kafka:BootstrapServers"] = "redpanda:9092"
        };
        var services = new ServiceCollection();
        services.AddCoreInfrastructure(
            configuration,
            new CoreInfrastructureOptions { ServiceProfile = CoreServiceProfile.DataIngestion });

        Assert.Contains(services, x =>
            x.ServiceType == typeof(IKafkaCompatibleProducer) &&
            x.ImplementationType == typeof(ConfluentKafkaProducer));
        Assert.DoesNotContain(services, x =>
            x.ServiceType.FullName?.Contains("Redis", StringComparison.OrdinalIgnoreCase) == true);
        using (var provider = services.BuildServiceProvider())
        {
            var transport = provider.GetRequiredService<KafkaTransportOptions>();
            Assert.Equal(Confluent.Kafka.SecurityProtocol.Ssl, transport.SecurityProtocol);
            Assert.Equal(30_000, transport.DeliveryTimeoutMilliseconds);
            Assert.Equal(10_000, transport.SocketTimeoutMilliseconds);
            Assert.Equal(5, transport.MaximumSendRetries);
        }

        var plaintext = new ConfigurationManager
        {
            ["Messaging:Kafka:BootstrapServers"] = "redpanda:9092",
            ["Messaging:Kafka:SecurityProtocol"] = "Plaintext"
        };
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCoreInfrastructure(
            plaintext,
            new CoreInfrastructureOptions { ServiceProfile = CoreServiceProfile.DataIngestion }));

        var invalidLimit = new ConfigurationManager
        {
            ["Messaging:Kafka:BootstrapServers"] = "redpanda:9092",
            ["Messaging:Kafka:MaximumPayloadBytes"] = "1000001"
        };
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCoreInfrastructure(
            invalidLimit,
            new CoreInfrastructureOptions { ServiceProfile = CoreServiceProfile.DataIngestion }));
    }

    [Theory]
    [InlineData("Server=sql.internal;Database=Post;Encrypt=false;TrustServerCertificate=false;Connect Timeout=15")]
    [InlineData("Server=sql.internal;Database=Post;Encrypt=true;TrustServerCertificate=true;Connect Timeout=15")]
    public void ProductionSqlServerRejectsUnprotectedTransport(string connectionString)
    {
        var configuration = new ConfigurationManager
        {
            ["Messaging:Kafka:BootstrapServers"] = "broker.internal:9093",
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] = AuthenticatedRedisConnectionString,
            ["Redis:RequireTls"] = "true"
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
            {
                Environment = CoreInfrastructureEnvironment.Production,
                ServiceProfile = CoreServiceProfile.ParcelLifecycle,
                DatabaseProvider = CoreDatabaseProvider.SqlServer,
                ParcelLifecycleConnectionString = connectionString,
                EnableParcelEventConsumer = false,
                EnableAttachmentMetadataConsumer = false,
                EnablePendingAttachmentReconciliation = false
            }));

        Assert.Contains("encrypted transport", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingIdentityProfilesPassStrictContainerValidation()
    {
        var configuration = new ConfigurationManager
        {
            ["Auth:Issuer"] = "post-core",
            ["Auth:Audience"] = "post-platform",
            ["Auth:SigningKey"] = "01234567890123456789012345678901",
            ["Messaging:Kafka:BootstrapServers"] = "redpanda:9092"
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCoreApplication(CoreApplicationProfile.Identity);
        services.AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
        {
            ServiceProfile = CoreServiceProfile.Identity,
            DatabaseProvider = CoreDatabaseProvider.Sqlite,
            IdentityConnectionString = "Data Source=:memory:"
        });

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAdminAuthService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAdminProvisioningService>());
    }

    [Fact]
    public void ProductionParcelProfileRegistersBothTypedConsumersWithAlignedTopics()
    {
        var configuration = new ConfigurationManager
        {
            ["Messaging:Kafka:BootstrapServers"] = "redpanda:9092",
            ["Messaging:Topics:ParcelEvents"] = "parcel.custom",
            ["Messaging:Topics:ImageMetadata"] = "attachment.custom",
            ["Messaging:Kafka:AttachmentMetadataDeadLetterTopic"] = "attachment.custom.dlq",
            ["Messaging:Kafka:AttachmentMetadataGroupId"] = "attachment-group",
            ["Messaging:Kafka:ConsumerFailureRetrySeconds"] = "3",
            ["Messaging:Kafka:ConsumerMaximumDeliveryAttempts"] = "7",
            ["Messaging:Kafka:MaximumPayloadBytes"] = "750000",
            ["Core:PendingAttachments:PollingSeconds"] = "11",
            ["Core:PendingAttachments:BaseRetrySeconds"] = "4",
            ["Core:PendingAttachments:MaximumRetrySeconds"] = "40",
            ["Core:PendingAttachments:MaximumAttempts"] = "8",
            ["Core:PendingAttachments:MaximumAgeHours"] = "12",
            ["Core:PendingAttachments:BatchSize"] = "25",
            ["Core:PendingAttachments:DeadLetterTopic"] = "pending.custom.dlq",
            ["Redis:Enabled"] = "true",
            ["Redis:ConnectionString"] = AuthenticatedRedisConnectionString,
            ["Redis:RequireTls"] = "true"
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCoreApplication(CoreApplicationProfile.ParcelLifecycle);
        services.AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
        {
            ServiceProfile = CoreServiceProfile.ParcelLifecycle,
            DatabaseProvider = CoreDatabaseProvider.Sqlite,
            ParcelLifecycleConnectionString = "Data Source=:memory:"
        });

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IParcelEventDeliveryProcessor) &&
            descriptor.ImplementationType == typeof(ParcelEventDeliveryProcessor));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IAttachmentMetadataDeliveryProcessor) &&
            descriptor.ImplementationType == typeof(AttachmentMetadataDeliveryProcessor));

        using var provider = services.BuildServiceProvider();
        var attachmentOptions = provider.GetRequiredService<AttachmentMetadataConsumerOptions>();
        Assert.Equal("attachment.custom", attachmentOptions.Topic);
        Assert.Equal("attachment.custom.dlq", attachmentOptions.DeadLetterTopic);
        Assert.Equal("attachment-group", attachmentOptions.GroupId);
        Assert.Equal(TimeSpan.FromSeconds(3), attachmentOptions.FailureRetryDelay);
        Assert.Equal(7, attachmentOptions.MaximumDeliveryAttempts);
        Assert.Equal(750_000, attachmentOptions.MaximumPayloadBytes);
        Assert.Equal(750_000, provider.GetRequiredService<ParcelEventConsumerOptions>().MaximumPayloadBytes);
        Assert.Equal(750_000, provider.GetRequiredService<KafkaTransportOptions>().MaximumPayloadBytes);
        var pendingOptions = provider.GetRequiredService<PendingAttachmentReconciliationOptions>();
        Assert.Equal(TimeSpan.FromSeconds(11), pendingOptions.PollingInterval);
        Assert.Equal(TimeSpan.FromSeconds(4), pendingOptions.BaseRetryDelay);
        Assert.Equal(TimeSpan.FromSeconds(40), pendingOptions.MaximumRetryDelay);
        Assert.Equal(8, pendingOptions.MaximumAttempts);
        Assert.Equal(TimeSpan.FromHours(12), pendingOptions.MaximumAge);
        Assert.Equal(25, pendingOptions.BatchSize);
        Assert.Equal("pending.custom.dlq", pendingOptions.DeadLetterTopic);
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        Assert.Contains(hostedServices, service => service is CoreParcelEventConsumerBackgroundService);
        Assert.Contains(hostedServices, service => service is CoreAttachmentMetadataConsumerBackgroundService);
        Assert.Contains(hostedServices, service => service is CorePendingAttachmentReconciliationBackgroundService);
    }

    [Fact]
    public void ProductionConsumerConfigurationRejectsNonPositiveRetryValues()
    {
        var configuration = new ConfigurationManager
        {
            ["Messaging:Kafka:BootstrapServers"] = "redpanda:9092",
            ["Messaging:Kafka:ConsumerFailureRetrySeconds"] = "0"
        };

        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
            {
                ServiceProfile = CoreServiceProfile.ParcelLifecycle,
                DatabaseProvider = CoreDatabaseProvider.Sqlite,
                ParcelLifecycleConnectionString = "Data Source=:memory:"
            }));

        configuration["Messaging:Kafka:ConsumerFailureRetrySeconds"] = "2";
        configuration["Core:PendingAttachments:BaseRetrySeconds"] = "10";
        configuration["Core:PendingAttachments:MaximumRetrySeconds"] = "5";
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
            {
                ServiceProfile = CoreServiceProfile.ParcelLifecycle,
                DatabaseProvider = CoreDatabaseProvider.Sqlite,
                ParcelLifecycleConnectionString = "Data Source=:memory:"
            }));
    }

    [Fact]
    public void ProductionEdgeHealthProfileRegistersValidatedPeriodicMonitor()
    {
        var configuration = new ConfigurationManager
        {
            ["Messaging:Kafka:BootstrapServers"] = "redpanda:9092",
            ["Core:EdgeHealthMonitor:CheckIntervalSeconds"] = "15",
            ["Core:EdgeHealthMonitor:StaleThresholdSeconds"] = "180"
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCoreApplication(CoreApplicationProfile.EdgeHealth);
        services.AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
        {
            ServiceProfile = CoreServiceProfile.EdgeHealth,
            DatabaseProvider = CoreDatabaseProvider.Sqlite,
            EdgeHealthConnectionString = "Data Source=:memory:"
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<EdgeHealthMonitorOptions>();
        Assert.Equal(TimeSpan.FromSeconds(15), options.CheckInterval);
        Assert.Equal(TimeSpan.FromMinutes(3), options.StaleThreshold);
        Assert.Contains(
            provider.GetServices<IHostedService>(),
            service => service is CoreEdgeHealthMonitorBackgroundService);

        configuration["Core:EdgeHealthMonitor:StaleThresholdSeconds"] = "-1";
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCoreInfrastructure(configuration, new CoreInfrastructureOptions
            {
                ServiceProfile = CoreServiceProfile.EdgeHealth,
                DatabaseProvider = CoreDatabaseProvider.Sqlite,
                EdgeHealthConnectionString = "Data Source=:memory:"
            }));
    }
}
