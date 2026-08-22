using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Post.BuildingBlocks.Serialization;
using Post.Contracts;
using Post.Contracts.V1.EdgeCore;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Resilience;

namespace Post.Edge.Infrastructure.Integrations.Core;

public sealed class CoreApiClient(
    HttpClient httpClient,
    ICoreAccessTokenProvider tokens,
    IOptions<CoreApiOptions> options) : ICoreApiClient, ICoreEdgeHealthClient
{
    private static readonly JsonSerializerOptions JsonOptions = PostJsonDefaults.Create(strict: false);

    public async Task<CoreInboundLookupResult> LookupInboundAsync(
        CoreInboundLookupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deviceId = ParseSortingDeviceId(request.DeviceId);
        var idempotencyKey = request.OperationId.ToString("D");
        using var response = await CoreAuthenticatedSender.SendAsync(
            httpClient,
            tokens,
            () =>
            {
                var message = new HttpRequestMessage(HttpMethod.Post, options.Value.InboundLookupPath)
                {
                    Content = JsonContent.Create(new InboundQueryWireRequest(
                        idempotencyKey,
                        request.Barcodes,
                        request.ParcelType,
                        request.ServiceType,
                        request.OriginCenterCode,
                        request.DestinationCenterCode,
                        deviceId,
                        options.Value.ExchangeCenterCode,
                        new PhysicalAttributesWire(
                            (request.WeightGrams ?? 0) / 1_000m,
                            (request.LengthMillimeters ?? 0) / 10m,
                            (request.WidthMillimeters ?? 0) / 10m,
                            (request.HeightMillimeters ?? 0) / 10m),
                        request.EdgeEventTime,
                        request.CorrelationId),
                        options: JsonOptions)
                };
                AddOperationHeaders(message, idempotencyKey, request.CorrelationId);
                return message;
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await BoundedHttpResponse.ReadJsonAsync<InboundQueryWireResponse>(
                          response.Content,
                          options.Value.MaximumResponseBytes,
                          JsonOptions,
                          cancellationToken)
                      ?? throw new InvalidOperationException("Core inbound response was empty.");
        var status = ReadEnumName(payload.DeviceStatus);
        return new CoreInboundLookupResult(
            status switch
            {
                "Returned" => CoreParcelDisposition.Returned,
                "Refused" => CoreParcelDisposition.Refused,
                _ => CoreParcelDisposition.Normal
            },
            payload.Discrepancies is { Count: > 0 }
                ? string.Join(',', payload.Discrepancies.Select(item => ReadEnumName(item.Attribute)))
                : null,
            payload.DestinationCenterCode,
            payload.ReadingRecordId);
    }

    public async Task PublishAsync(
        string messageType,
        string payloadJson,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var mapped = MapPublishPayload(messageType, payloadJson, idempotencyKey);
        using var response = await CoreAuthenticatedSender.SendAsync(
            httpClient,
            tokens,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, mapped.Path);
                AddOperationHeaders(request, idempotencyKey, mapped.CorrelationId);
                request.Headers.TryAddWithoutValidation("X-Edge-Message-Type", messageType);
                request.Content = new StringContent(mapped.Json, Encoding.UTF8, "application/json");
                return request;
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task RegisterAsync(CoreEdgeRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        await SendHealthAsync(options.Value.RegistrationPath, registration, cancellationToken);
    }

    public async Task SendHeartbeatAsync(CoreEdgeHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        await SendHealthAsync(options.Value.HeartbeatPath, heartbeat, cancellationToken);
    }

    internal string ResolvePublishPath(string messageType) => messageType switch
    {
        ContractMessageTypes.EdgeBagClosed or "EdgeBagClosed" => options.Value.BagsPath,
        ContractMessageTypes.EdgeDispatchClosed or "EdgeDispatchClosed" => options.Value.DispatchesPath,
        ContractMessageTypes.EdgeParcelReadingSubmitted or
            ContractMessageTypes.EdgeParcelStatusChanged => options.Value.ParcelEventsPath,
        "OperationalResultCaptured" => options.Value.OperationalResultsPath,
        "edge.device-ip-binding.changed" or
            "edge.device.connected" or
            "edge.device.authenticated" or
            "edge.device.authentication-rejected" or
            "edge.device.disconnected" => options.Value.EventsPath,
        _ => throw new InvalidOperationException($"Core route is not defined for message type '{messageType}'.")
    };

    private MappedPayload MapPublishPayload(string messageType, string payloadJson, string idempotencyKey)
    {
        var path = ResolvePublishPath(messageType);
        if (messageType is ContractMessageTypes.EdgeBagClosed or "EdgeBagClosed")
        {
            var payload = Deserialize<BagClosedV1>(payloadJson, messageType);
            var correlation = payload.OperationId.ToString("D");
            return Serialize(path, correlation, new BagWireRequest(
                payload.BagBarcode,
                payload.OriginCenterCode,
                payload.DestinationCode,
                ParseSortingDeviceId(payload.DeviceId),
                payload.ParcelBarcodes,
                payload.SealNumber,
                payload.TransportType,
                payload.ClosedAtUtc,
                idempotencyKey,
                correlation));
        }

        if (messageType is ContractMessageTypes.EdgeDispatchClosed or "EdgeDispatchClosed")
        {
            var payload = Deserialize<DispatchClosedV1>(payloadJson, messageType);
            var correlation = payload.OperationId.ToString("D");
            return Serialize(path, correlation, new DispatchWireRequest(
                payload.DispatchId,
                payload.DestinationCode,
                payload.BagBarcodes,
                payload.ClosedAtUtc,
                idempotencyKey,
                correlation));
        }

        if (messageType == ContractMessageTypes.EdgeParcelReadingSubmitted)
        {
            var payload = Deserialize<ParcelReadingSubmittedV1>(payloadJson, messageType);
            return ParcelEvent(path, idempotencyKey, payload.OperationId, payload.CanonicalBarcode,
                payload.Classification switch
                {
                    CoreReadingClassificationV1.Returned => "Returned",
                    CoreReadingClassificationV1.ReturnToOrigin => "Refused",
                    _ when payload.RegistrationStatus is CoreOperationStatusV1.Failed or
                        CoreOperationStatusV1.Rejected => "Error",
                    _ => "Success"
                }, payload.ReadAtUtc);
        }

        if (messageType == ContractMessageTypes.EdgeParcelStatusChanged)
        {
            var payload = Deserialize<ParcelStatusChangedV1>(payloadJson, messageType);
            return ParcelEvent(path, idempotencyKey, payload.OperationId, payload.ParcelBarcode,
                payload.Error is null ? "Success" : "Error", payload.ChangedAtUtc);
        }

        if (messageType == "edge.device-ip-binding.changed")
        {
            var payload = Deserialize<DeviceBindingAuditPayload>(payloadJson, messageType);
            return Serialize(path, payload.CorrelationId, new OperationalEventWireRequest(
                "DeviceIpBindingChanged",
                RequireEdgeId(),
                Guid.TryParse(payload.DeviceId, out var sortingDeviceId) ? sortingDeviceId : null,
                idempotencyKey,
                payload.CorrelationId,
                payload.OccurredAt));
        }

        if (messageType is "edge.device.connected" or
            "edge.device.authenticated" or
            "edge.device.authentication-rejected" or
            "edge.device.disconnected")
        {
            var payload = Deserialize<CoreDeviceOperationalEvent>(payloadJson, messageType);
            var eventType = messageType switch
            {
                "edge.device.connected" => "Connected",
                "edge.device.authenticated" => "Authenticated",
                "edge.device.authentication-rejected" => "AuthenticationRejected",
                _ => "Disconnected"
            };
            return Serialize(path, payload.CorrelationId, new OperationalEventWireRequest(
                eventType,
                RequireEdgeId(),
                Guid.TryParse(payload.DeviceId, out var sortingDeviceId) ? sortingDeviceId : null,
                idempotencyKey,
                payload.CorrelationId,
                payload.OccurredAt));
        }

        if (messageType == "OperationalResultCaptured")
        {
            var payload = Deserialize<CoreOperationalResultMessage>(payloadJson, messageType);
            return Serialize(path, payload.CorrelationId, new OperationalResultWireRequest(
                RequireEdgeId(),
                RequireExchangeCenterCode(),
                idempotencyKey,
                payload.CorrelationId,
                payload.ParcelBarcode,
                payload.CallResult,
                payload.Success,
                payload.ErrorCode,
                payload.ErrorMessage,
                payload.CalledAt,
                payload.RespondedAt,
                payload.Attempts,
                payload.FinalStatus));
        }

        throw new InvalidOperationException(
            $"Core wire mapping is not defined for message type '{messageType}'.");
    }

    private MappedPayload ParcelEvent(
        string path,
        string idempotencyKey,
        Guid operationId,
        string barcode,
        string status,
        DateTimeOffset occurredAt)
    {
        var correlation = operationId.ToString("D");
        return Serialize(path, correlation, new ParcelEventWireRequest(
            operationId,
            RequireEdgeId(),
            RequireExchangeCenterCode(),
            null,
            idempotencyKey,
            correlation,
            new ParcelFinalPayloadWire(barcode, status),
            occurredAt));
    }

    private async Task SendHealthAsync<T>(string path, T payload, CancellationToken cancellationToken)
    {
        using var response = await CoreAuthenticatedSender.SendAsync(
            httpClient,
            tokens,
            () => HealthRequest(path, payload),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage HealthRequest<T>(string path, T payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        EdgeHttpResilience.MarkReplaySafe(request);
        return request;
    }

    private static MappedPayload Serialize<T>(string path, string correlationId, T payload) =>
        new(path, correlationId, JsonSerializer.Serialize(payload, JsonOptions));

    private static T Deserialize<T>(string payloadJson, string messageType) =>
        JsonSerializer.Deserialize<T>(payloadJson, JsonOptions)
        ?? throw new InvalidOperationException($"Core payload for '{messageType}' was empty.");

    private static Guid ParseSortingDeviceId(string deviceId) =>
        Guid.TryParse(deviceId, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidOperationException(
                "Connected Core integration requires the sorting device id from bootstrap to be a GUID.");

    private Guid RequireEdgeId() => options.Value.EdgeId != Guid.Empty
        ? options.Value.EdgeId
        : throw new InvalidOperationException("Connected Core integration requires Edge:CoreApi:EdgeId.");

    private string RequireExchangeCenterCode() => !string.IsNullOrWhiteSpace(options.Value.ExchangeCenterCode)
        ? options.Value.ExchangeCenterCode
        : throw new InvalidOperationException(
            "Connected Core integration requires Edge:CoreApi:ExchangeCenterCode.");

    private static string ReadEnumName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number when value.TryGetInt32(out var numeric) => numeric switch
        {
            3 => "Returned",
            4 => "Refused",
            _ => numeric.ToString(System.Globalization.CultureInfo.InvariantCulture)
        },
        _ => string.Empty
    };

    private static void AddOperationHeaders(
        HttpRequestMessage request,
        string idempotencyKey,
        string? correlationId)
    {
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (!string.IsNullOrWhiteSpace(correlationId))
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", correlationId);
    }

    private sealed record MappedPayload(string Path, string CorrelationId, string Json);
    private sealed record PhysicalAttributesWire(decimal WeightKg, decimal LengthCm, decimal WidthCm, decimal HeightCm);
    private sealed record InboundQueryWireRequest(
        string IdempotencyKey,
        IReadOnlyCollection<string> Barcodes,
        string ParcelType,
        string ServiceType,
        string OriginCenterCode,
        string DestinationCenterCode,
        Guid DeviceId,
        string ExchangeCenterCode,
        PhysicalAttributesWire PhysicalAttributes,
        DateTimeOffset EdgeEventTime,
        string CorrelationId);
    private sealed record InboundQueryWireResponse(
        JsonElement DeviceStatus,
        string DestinationCenterCode,
        Guid ReadingRecordId,
        IReadOnlyCollection<DiscrepancyWire>? Discrepancies);
    private sealed record DiscrepancyWire(JsonElement Attribute);
    private sealed record BagWireRequest(
        string BagBarcode,
        string OriginCenterCode,
        string DestinationCenterCode,
        Guid CreatedByDeviceId,
        IReadOnlyCollection<string> MemberBarcodes,
        string SealNumber,
        string TransportType,
        DateTimeOffset ClosedAt,
        string IdempotencyKey,
        string CorrelationId);
    private sealed record DispatchWireRequest(
        string DispatchId,
        string DestinationCenterCode,
        IReadOnlyCollection<string> BagBarcodes,
        DateTimeOffset ClosedAt,
        string IdempotencyKey,
        string CorrelationId);
    private sealed record ParcelEventWireRequest(
        Guid EventId,
        Guid EdgeId,
        string ExchangeCenterCode,
        string? Topic,
        string IdempotencyKey,
        string CorrelationId,
        ParcelFinalPayloadWire Payload,
        DateTimeOffset OccurredAt);
    private sealed record ParcelFinalPayloadWire(string ParcelBarcode, string FinalStatus);
    private sealed record DeviceBindingAuditPayload(
        string DeviceId,
        int BindingCount,
        string Actor,
        string CorrelationId,
        DateTimeOffset OccurredAt);
    private sealed record OperationalEventWireRequest(
        string EventType,
        Guid EdgeId,
        Guid? DeviceId,
        string IdempotencyKey,
        string CorrelationId,
        DateTimeOffset OccurredAt);
    private sealed record OperationalResultWireRequest(
        Guid EdgeId,
        string ExchangeCenterCode,
        string IdempotencyKey,
        string CorrelationId,
        string? ParcelBarcode,
        string CallResult,
        bool Success,
        string? ErrorCode,
        string? ErrorMessage,
        DateTimeOffset CalledAt,
        DateTimeOffset RespondedAt,
        int Attempts,
        string FinalStatus);
}
