using Post.Core.Domain.Common;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Domain.Operations;

public sealed class EdgeHealthStatus : IAggregateRoot
{
    private EdgeHealthStatus()
    {
        SoftwareVersion = string.Empty;
    }

    private EdgeHealthStatus(
        EdgeId edgeId,
        ExchangeCenterCode exchangeCenterCode,
        DateTimeOffset registeredAt,
        string softwareVersion,
        long configurationVersion)
    {
        if (configurationVersion < 0)
        {
            throw new DomainRuleException("Configuration version cannot be negative.");
        }

        EdgeId = edgeId;
        ExchangeCenterCode = exchangeCenterCode;
        LastHeartbeat = registeredAt;
        SoftwareVersion = Required(softwareVersion, nameof(softwareVersion), 64);
        ConfigurationVersion = configurationVersion;
        ConnectionState = EdgeConnectionState.Online;
        LastSuccessfulSync = registeredAt;
    }

    public EdgeId EdgeId { get; private set; }

    public ExchangeCenterCode ExchangeCenterCode { get; private set; }

    public DateTimeOffset LastHeartbeat { get; private set; }

    public string SoftwareVersion { get; private set; }

    public long ConfigurationVersion { get; private set; }

    public long AvailableDiskBytes { get; private set; }

    public EdgeConnectionState ConnectionState { get; private set; }

    public int PendingCount { get; private set; }

    public int FailedCount { get; private set; }

    public int DeadLetterCount { get; private set; }

    public DateTimeOffset? LastSuccessfulSync { get; private set; }

    public static EdgeHealthStatus Register(
        EdgeId edgeId,
        ExchangeCenterCode exchangeCenterCode,
        DateTimeOffset registeredAt,
        string softwareVersion,
        long configurationVersion) =>
        new(edgeId, exchangeCenterCode, registeredAt, softwareVersion, configurationVersion);

    public void ApplyHeartbeat(
        DateTimeOffset heartbeatAt,
        string softwareVersion,
        long configurationVersion,
        long availableDiskBytes,
        int pendingCount,
        int failedCount,
        int deadLetterCount,
        DateTimeOffset? lastSuccessfulSync)
    {
        if (heartbeatAt < LastHeartbeat)
        {
            throw new DomainRuleException("An out-of-order heartbeat cannot replace a newer heartbeat.");
        }

        if (configurationVersion < 0 || availableDiskBytes < 0 ||
            pendingCount < 0 || failedCount < 0 || deadLetterCount < 0)
        {
            throw new DomainRuleException(
                "Heartbeat counters, configuration version, and available disk bytes cannot be negative.");
        }

        LastHeartbeat = heartbeatAt;
        SoftwareVersion = Required(softwareVersion, nameof(softwareVersion), 64);
        ConfigurationVersion = configurationVersion;
        AvailableDiskBytes = availableDiskBytes;
        PendingCount = pendingCount;
        FailedCount = failedCount;
        DeadLetterCount = deadLetterCount;
        LastSuccessfulSync = lastSuccessfulSync;
        ConnectionState = EdgeConnectionState.Online;
    }

    public bool MarkOfflineWhenStale(DateTimeOffset now, TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
        {
            throw new DomainRuleException("Health threshold must be positive.");
        }

        if (ConnectionState != EdgeConnectionState.Online || now - LastHeartbeat <= threshold)
        {
            return false;
        }

        ConnectionState = EdgeConnectionState.Offline;
        return true;
    }

    public void Disable() => ConnectionState = EdgeConnectionState.Disabled;

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }
}

public sealed class OperationalEvent
{
    private OperationalEvent()
    {
    }

    private OperationalEvent(
        Guid id,
        OperationalEventType eventType,
        EdgeId edgeId,
        DeviceId? deviceId,
        CorrelationId correlationId,
        DateTimeOffset occurredAt)
    {
        Id = id;
        EventType = eventType;
        EdgeId = edgeId;
        DeviceId = deviceId;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    public OperationalEventType EventType { get; private set; }

    public EdgeId EdgeId { get; private set; }

    public DeviceId? DeviceId { get; private set; }

    public CorrelationId CorrelationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static OperationalEvent Create(
        OperationalEventType eventType,
        EdgeId edgeId,
        DeviceId? deviceId,
        CorrelationId correlationId,
        DateTimeOffset occurredAt) =>
        new(Guid.NewGuid(), eventType, edgeId, deviceId, correlationId, occurredAt);
}

public sealed class OperationalResult : IAggregateRoot
{
    private OperationalResult()
    {
        CallResult = string.Empty;
        ErrorCode = string.Empty;
        ErrorMessage = string.Empty;
        RequestHash = string.Empty;
    }

    private OperationalResult(
        Guid id,
        EdgeId edgeId,
        ExchangeCenterCode exchangeCenterCode,
        IdempotencyKey idempotencyKey,
        string requestHash,
        CorrelationId correlationId,
        ParcelBarcode? parcelBarcode,
        string callResult,
        bool success,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset calledAt,
        DateTimeOffset respondedAt,
        int attempts,
        OperationalFinalStatus finalStatus)
    {
        if (respondedAt < calledAt)
        {
            throw new DomainRuleException("Response time cannot precede call time.");
        }

        if (attempts <= 0)
        {
            throw new DomainRuleException("Attempt count must be positive.");
        }

        Id = id;
        EdgeId = edgeId;
        ExchangeCenterCode = exchangeCenterCode;
        IdempotencyKey = idempotencyKey;
        RequestHash = Required(requestHash, nameof(requestHash), 128);
        CorrelationId = correlationId;
        ParcelBarcode = parcelBarcode;
        CallResult = Required(callResult, nameof(callResult), 128);
        Success = success;
        ErrorCode = Optional(errorCode, 128);
        ErrorMessage = Optional(errorMessage, 2048);
        CalledAt = calledAt;
        RespondedAt = respondedAt;
        Attempts = attempts;
        FinalStatus = finalStatus;
    }

    public Guid Id { get; private set; }

    public EdgeId EdgeId { get; private set; }

    public ExchangeCenterCode ExchangeCenterCode { get; private set; }

    public IdempotencyKey IdempotencyKey { get; private set; }

    public string RequestHash { get; private set; }

    public CorrelationId CorrelationId { get; private set; }

    public ParcelBarcode? ParcelBarcode { get; private set; }

    public string CallResult { get; private set; }

    public bool Success { get; private set; }

    public string ErrorCode { get; private set; }

    public string ErrorMessage { get; private set; }

    public DateTimeOffset CalledAt { get; private set; }

    public DateTimeOffset RespondedAt { get; private set; }

    public int Attempts { get; private set; }

    public OperationalFinalStatus FinalStatus { get; private set; }

    public static OperationalResult Create(
        EdgeId edgeId,
        ExchangeCenterCode exchangeCenterCode,
        IdempotencyKey idempotencyKey,
        string requestHash,
        CorrelationId correlationId,
        ParcelBarcode? parcelBarcode,
        string callResult,
        bool success,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset calledAt,
        DateTimeOffset respondedAt,
        int attempts,
        OperationalFinalStatus finalStatus) =>
        new(
            Guid.NewGuid(),
            edgeId,
            exchangeCenterCode,
            idempotencyKey,
            requestHash,
            correlationId,
            parcelBarcode,
            callResult,
            success,
            errorCode,
            errorMessage,
            calledAt,
            respondedAt,
            attempts,
            finalStatus);

    public bool IsReplay(string requestHash) =>
        string.Equals(RequestHash, requestHash, StringComparison.Ordinal);

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }

    private static string Optional(string? value, int maxLength)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length > maxLength)
        {
            throw new DomainRuleException($"Value cannot exceed {maxLength} characters.");
        }

        return result;
    }
}
