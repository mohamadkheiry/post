using Post.Core.Domain.Common;

namespace Post.Core.EdgeHealth.Api;

public sealed record RegisterEdgeRequest(
    Guid EdgeId,
    string ExchangeCenterCode,
    string SoftwareVersion,
    long ConfigurationVersion,
    DateTimeOffset RegisteredAt);

public sealed record EdgeHeartbeatRequest(
    Guid EdgeId,
    DateTimeOffset HeartbeatAt,
    string SoftwareVersion,
    long ConfigurationVersion,
    long AvailableDiskBytes,
    int PendingCount,
    int FailedCount,
    int DeadLetterCount,
    DateTimeOffset? LastSuccessfulSync);

public sealed record OperationalEventRequest(
    OperationalEventType EventType,
    Guid EdgeId,
    Guid? DeviceId,
    string? IdempotencyKey,
    string? CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record PagedResponse<T>(
    IReadOnlyCollection<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class EdgeHealthApiMarker;
