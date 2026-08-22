namespace Post.Contracts.V1.EdgeCore;

/// <summary>A versioned, idempotent message sent between an edge node and core.</summary>
/// <typeparam name="TPayload">Concrete V1 payload.</typeparam>
/// <param name="SchemaVersion">Contract schema version.</param>
/// <param name="MessageId">Unique message and idempotency identifier.</param>
/// <param name="MessageType">Stable message type.</param>
/// <param name="CorrelationId">End-to-end correlation identifier.</param>
/// <param name="OperationId">Optional stable business idempotency key shared by messages caused by one operation.</param>
/// <param name="CausationId">Message or operation that caused this message.</param>
/// <param name="OccurredAtUtc">UTC business occurrence time.</param>
/// <param name="EdgeId">Registered edge-node identifier.</param>
/// <param name="CenterCode">Exchange-center code.</param>
/// <param name="Sequence">Strictly increasing edge sequence used for ordered replay.</param>
/// <param name="Payload">Typed message payload.</param>
public sealed record EdgeCoreEnvelopeV1<TPayload>(
    string SchemaVersion,
    Guid MessageId,
    string MessageType,
    string CorrelationId,
    Guid? OperationId,
    Guid? CausationId,
    DateTimeOffset OccurredAtUtc,
    string EdgeId,
    string CenterCode,
    long Sequence,
    TPayload Payload);

/// <summary>Factory for edge/core envelopes without static members on a generic type.</summary>
public static class EdgeCoreEnvelopeV1
{
    /// <summary>Creates a V1 edge/core envelope while enforcing transport invariants.</summary>
    public static EdgeCoreEnvelopeV1<TPayload> Create<TPayload>(
        string messageType,
        string correlationId,
        DateTimeOffset occurredAtUtc,
        string edgeId,
        string centerCode,
        long sequence,
        TPayload payload,
        Guid? operationId = null,
        Guid? causationId = null,
        Guid? messageId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(edgeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(centerCode);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Operation id cannot be an empty GUID.", nameof(operationId));
        }

        return new EdgeCoreEnvelopeV1<TPayload>(
            ContractVersions.V1,
            messageId ?? Guid.NewGuid(),
            messageType,
            correlationId,
            operationId,
            causationId,
            occurredAtUtc.ToUniversalTime(),
            edgeId,
            centerCode,
            sequence,
            payload);
    }
}
