namespace Post.Contracts.V1.Device;

/// <summary>
/// Exact device-protocol V1 envelope plus an optional backward-compatible operation id.
/// SignalR invocation id remains transport-level and is not represented here.
/// </summary>
/// <typeparam name="TPayload">Concrete V1 payload type.</typeparam>
/// <param name="ProtocolVersion">Exact device protocol version.</param>
/// <param name="MessageType">Stable message type.</param>
/// <param name="CorrelationId">Unique request/response correlation identifier.</param>
/// <param name="Timestamp">Informational producer timestamp.</param>
/// <param name="Payload">Typed message payload.</param>
/// <param name="OperationId">Optional stable idempotency key retained across retries with new correlation ids.</param>
public sealed record DeviceEnvelopeV1<TPayload>(
    string ProtocolVersion,
    string MessageType,
    Guid CorrelationId,
    DateTimeOffset Timestamp,
    TPayload Payload,
    Guid? OperationId = null);

/// <summary>Factory for device envelopes without static members on a generic type.</summary>
public static class DeviceEnvelopeV1
{
    /// <summary>Creates a V1 envelope while enforcing transport-level invariants.</summary>
    public static DeviceEnvelopeV1<TPayload> Create<TPayload>(
        string messageType,
        Guid correlationId,
        DateTimeOffset timestamp,
        TPayload payload,
        Guid? operationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentOutOfRangeException.ThrowIfEqual(correlationId, Guid.Empty);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation id cannot be an empty GUID.", nameof(operationId));
        }

        ArgumentNullException.ThrowIfNull(payload);

        return new DeviceEnvelopeV1<TPayload>(
            ContractVersions.V1,
            messageType,
            correlationId,
            timestamp,
            payload,
            operationId);
    }
}

/// <summary>A protocol-level error payload returned without closing an authenticated connection.</summary>
/// <param name="Code">Stable protocol error code.</param>
/// <param name="Message">Human-readable error.</param>
/// <param name="Details">Optional validation details.</param>
public sealed record DeviceProtocolErrorV1(string Code, string Message, string? Details = null);
