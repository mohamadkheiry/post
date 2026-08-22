using Post.Core.Domain.Common;

namespace Post.Core.OperationalResult.Api;

public sealed record OperationalResultRequest(
    Guid EdgeId,
    string ExchangeCenterCode,
    string? IdempotencyKey,
    string? CorrelationId,
    string? ParcelBarcode,
    string CallResult,
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset CalledAt,
    DateTimeOffset RespondedAt,
    int Attempts,
    OperationalFinalStatus FinalStatus);

public sealed class OperationalResultApiMarker;
