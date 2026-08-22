using System.Text.Json;
using Post.Core.Domain.Common;

namespace Post.Core.DataIngestion.Api;

public sealed record ParcelEventRequest(
    Guid EventId,
    Guid EdgeId,
    string ExchangeCenterCode,
    string? Topic,
    string? IdempotencyKey,
    string? CorrelationId,
    ParcelFinalPayload Payload,
    DateTimeOffset OccurredAt);

public sealed record ParcelFinalPayload(
    string ParcelBarcode,
    CoreParcelStatus FinalStatus);

public sealed record ImageMetadataRequest(
    Guid EventId,
    Guid EdgeId,
    string ExchangeCenterCode,
    string ParcelBarcode,
    Guid ReadingRecordId,
    string ObjectKey,
    string ContentType,
    string AttachmentType,
    string? IdempotencyKey,
    string? CorrelationId,
    DateTimeOffset ReceivedAt);

public sealed record EdgeErrorRequest(
    Guid EventId,
    Guid EdgeId,
    string ExchangeCenterCode,
    string? Topic,
    string? IdempotencyKey,
    string? CorrelationId,
    JsonElement Payload,
    DateTimeOffset OccurredAt);

public sealed class DataIngestionApiMarker;
