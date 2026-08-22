namespace Post.Contracts;

/// <summary>
/// Stable schema-version identifiers used by all wire contracts.
/// </summary>
public static class ContractVersions
{
    /// <summary>The first public contract version.</summary>
    public const string V1 = "1.0";
}

/// <summary>
/// Canonical message type names. Values are persisted and therefore must never be renamed.
/// </summary>
public static class ContractMessageTypes
{
    /// <summary>Device authentication request.</summary>
    public const string DeviceAuthenticate = "auth";
    /// <summary>Parcel reading submitted by a device.</summary>
    public const string DeviceParcelRead = "inbound.register";
    /// <summary>Operational destination assignment.</summary>
    public const string DeviceDestinationAssign = "destination.assign";
    /// <summary>Bag closure request.</summary>
    public const string DeviceBagClose = "bag.close";
    /// <summary>Dispatch closure request.</summary>
    public const string DeviceDispatchClose = "dispatch.close";
    /// <summary>Protocol-level error returned by edge.</summary>
    public const string DeviceProtocolError = "protocol.error";
    /// <summary>Parcel reading sent from an edge node to core.</summary>
    public const string EdgeParcelReadingSubmitted = "edge.parcel-reading-submitted.v1";
    /// <summary>Parcel operational status sent from edge to core.</summary>
    public const string EdgeParcelStatusChanged = "edge.parcel-status-changed.v1";
    /// <summary>Closed bag sent from edge to core.</summary>
    public const string EdgeBagClosed = "edge.bag-closed.v1";
    /// <summary>Closed dispatch sent from edge to core.</summary>
    public const string EdgeDispatchClosed = "edge.dispatch-closed.v1";
    /// <summary>Operational audit event sent from edge to core.</summary>
    public const string EdgeOperationalEvent = "edge.operational-event.v1";
    /// <summary>Edge health heartbeat.</summary>
    public const string EdgeHeartbeat = "edge.heartbeat.v1";
}

/// <summary>SignalR hub method names fixed by device protocol V1.</summary>
public static class DeviceHubMethodsV1
{
    /// <summary>Authentication hub method.</summary>
    public const string Auth = "Auth";
    /// <summary>Inbound registration hub method.</summary>
    public const string RegisterInbound = "RegisterInbound";
    /// <summary>Destination assignment hub method.</summary>
    public const string AssignDestination = "AssignDestination";
    /// <summary>Bag closure hub method.</summary>
    public const string CloseBag = "CloseBag";
    /// <summary>Dispatch closure hub method.</summary>
    public const string CloseDispatch = "CloseDispatch";
}
