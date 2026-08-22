namespace Post.Edge.Infrastructure.Options;

public sealed class EdgeIntegrationOptions
{
    public const string SectionName = "Edge:Integrations";
    public string Profile { get; set; } = "LocalConnected";
    public string? Destination { get; set; }
    public string? Postal { get; set; }
    public string? Core { get; set; }
}

public sealed class EdgeStorageOptions
{
    public const string SectionName = "Edge:Storage";
    public string ConnectionString { get; set; } = "Data Source=edge.db";
    public bool ApplyMigrationsOnStartup { get; set; } = true;
    public long MinimumAvailableDiskBytes { get; set; } = 536_870_912;
    public bool EncryptionEnabled { get; set; }
    public string EncryptionKeyEnvironmentVariable { get; set; } = "POST_EDGE_SQLITE_KEY";
    public string PreviousEncryptionKeyEnvironmentVariable { get; set; } = "POST_EDGE_SQLITE_PREVIOUS_KEY";
    public string BackupDirectory { get; set; } = "edge-backups";
    public string? RestoreFromBackupPath { get; set; }
}

public sealed class DestinationApiOptions
{
    public const string SectionName = "Edge:DestinationApi";
    public string BaseAddress { get; set; } = "https://ecommwebhook.post.ir";
    public string TokenPath { get; set; } = "/api/v1/Users/Token";
    public string ParcelInfoPath { get; set; } = "/api/v1/Parcel/ParcelInfo";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromMinutes(20);
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaximumResponseBytes { get; set; } = 1_048_576;
}

public sealed class CoreApiOptions
{
    public const string SectionName = "Edge:CoreApi";
    public string BaseAddress { get; set; } = "http://localhost:8080";
    public Guid EdgeId { get; set; }
    public string ExchangeCenterCode { get; set; } = string.Empty;
    public string InboundLookupPath { get; set; } = "/api/edge/parcels/inbound-query";
    public string ParcelEventsPath { get; set; } = "/api/edge/parcel-events";
    public string BagsPath { get; set; } = "/api/edge/bags";
    public string DispatchesPath { get; set; } = "/api/edge/dispatches";
    public string EventsPath { get; set; } = "/api/edge/events";
    public string OperationalResultsPath { get; set; } = "/api/edge/operational-results";
    public string RegistrationPath { get; set; } = "/api/edge/registration";
    public string HeartbeatPath { get; set; } = "/api/edge/heartbeat";
    public string BootstrapPath { get; set; } = "/api/edge/config/bootstrap";
    public string ImagePresignedPath { get; set; } = "/api/edge/images/presigned-url";
    public string ImageMetadataPath { get; set; } = "/api/edge/images/metadata";
    public int ImageUploadLifetimeSeconds { get; set; } = 600;
    public string TokenPath { get; set; } = "/api/auth/edge-token";
    public string? LogicalDeviceCode { get; set; }
    public string? DeviceSecret { get; set; }
    public TimeSpan TokenRefreshSkew { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan DefaultTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);
    public bool UseFixedBearerToken { get; set; }
    public string? BearerToken { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public int MaximumResponseBytes { get; set; } = 8_388_608;
}

public sealed class PostalSqlOptions
{
    public const string SectionName = "Edge:PostalSql";
    public string? ConnectionString { get; set; }
    public int UserId { get; set; }
    public int SourceId { get; set; }
    public string DeviceIp { get; set; } = "127.0.0.1";
    public string? PostUsername { get; set; }
    public int BagServiceType { get; set; } = 1;
    public int BagType { get; set; } = 1;
    public int ServiceTypeId { get; set; } = 1;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public int CommandTimeoutSeconds { get; set; } = 30;
}

public sealed class PostalSoapOptions
{
    public const string SectionName = "Edge:PostalSoap";
    public string? Endpoint { get; set; }
    public string SoapAction { get; set; } = "http://tempuri.org/GetDistributeInfoData";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public int MaximumResponseCharacters { get; set; } = 1_048_576;
}

public sealed class EdgeResilienceOptions
{
    public const string SectionName = "Edge:Resilience";
    public int HttpMaxRetryAttempts { get; set; } = 2;
    public TimeSpan HttpRetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan HttpAttemptTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public double HttpCircuitFailureRatio { get; set; } = 0.5;
    public int HttpCircuitMinimumThroughput { get; set; } = 8;
    public TimeSpan HttpCircuitSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HttpCircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
    public int SqlMaxRetryAttempts { get; set; } = 2;
    public TimeSpan SqlRetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);
    public int SqlCircuitFailureThreshold { get; set; } = 4;
    public TimeSpan SqlCircuitBreakDuration { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed class EdgeWorkerOptions
{
    public const string SectionName = "Edge:Workers";
    public int BatchSize { get; set; } = 50;
    public int MaxOutboxAttempts { get; set; } = 8;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ConfigurationSyncInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool CleanupEnabled { get; set; }
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(30);
    public TimeSpan IdempotencyRetentionPeriod { get; set; } = TimeSpan.FromDays(90);
}

public sealed class EdgeSecurityOptions
{
    public const string SectionName = "Edge:Security";
    public bool AllowUnboundDevices { get; set; }
    public bool AllowInsecureDevelopmentIntegrations { get; set; }
    public List<string> AllowedInsecureDevelopmentHosts { get; set; } = [];
}

public sealed class EdgeLocalAdministratorOptions
{
    public const string SectionName = "Edge:LocalAdmin";
    public bool AuthenticationEnabled { get; set; } = true;
    public string InitialUserName { get; set; } = "admin";
    public string? InitialPassword { get; set; }
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromMinutes(30);
    public int LoginPermitLimit { get; set; } = 5;
    public TimeSpan LoginRateWindow { get; set; } = TimeSpan.FromMinutes(1);
    public long MaximumRequestBodyBytes { get; set; } = 4_096;
}

public sealed class EdgeAutoDispatchOptions
{
    public const string SectionName = "Edge:AutoDispatch";
    public bool Enabled { get; set; }
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(1);
    public int MinimumBagCount { get; set; } = 10;
    public TimeSpan MaximumBagAge { get; set; } = TimeSpan.FromHours(2);
    public List<string> DeviceIds { get; set; } = [];
    public List<string> DestinationCodes { get; set; } = [];
}

public sealed class EdgeMockOptions
{
    public const string SectionName = "Edge:Mock";
    public string CenterCode { get; set; } = "59544";
    public long ConfigurationVersion { get; set; } = 1;
    public List<MockDeviceOptions> Devices { get; set; } = [];
    public Dictionary<string, string> FourteenDigitDestinations { get; set; } = new(StringComparer.Ordinal);
    public List<string> InboundFailureBarcodes { get; set; } = [];
    public List<string> InboundPendingBarcodes { get; set; } = [];
    public List<string> BagFailureBarcodes { get; set; } = [];
    public bool BagServiceUnavailable { get; set; }
    public bool DispatchServiceUnavailable { get; set; }
}

public sealed class MockDeviceOptions
{
    public string DeviceId { get; set; } = "mock-device";
    public string LogicalId { get; set; } = "MOCK-01";
    public string Token { get; set; } = "replace-in-environment";
    public bool IsActive { get; set; } = true;
}
