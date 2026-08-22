namespace Post.Edge.Infrastructure.Persistence;

internal sealed class ParcelRow
{
    public Guid ReadId { get; set; }
    public string CanonicalBarcode { get; set; } = string.Empty;
    public string AliasesJson { get; set; } = "[]";
    public string DeviceId { get; set; } = string.Empty;
    public DateTimeOffset ReadTimestamp { get; set; }
    public int? WeightGrams { get; set; }
    public int? LengthMm { get; set; }
    public int? WidthMm { get; set; }
    public int? HeightMm { get; set; }
    public string? ParcelType { get; set; }
    public string? ServiceType { get; set; }
    public int State { get; set; }
    public string? DestinationCode { get; set; }
    public string? ChuteId { get; set; }
    public Guid? PreviousReadId { get; set; }
    public string? BagBarcode { get; set; }
    public string ErrorsJson { get; set; } = "[]";
    public bool CoreSynced { get; set; }
}

internal sealed class BagRow
{
    public string BagBarcode { get; set; } = string.Empty;
    public Guid LocalOperationId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DestinationCode { get; set; } = string.Empty;
    public string MemberBarcodesJson { get; set; } = "[]";
    public byte[] Label { get; set; } = [];
    public string SealNumber { get; set; } = string.Empty;
    public string TransportType { get; set; } = string.Empty;
    public int N { get; set; }
    public int P { get; set; }
    public int M { get; set; }
    public int Q { get; set; }
    public string ErrorsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public string? DispatchId { get; set; }
    public bool CoreConfirmed { get; set; }
}

internal sealed class PendingBagClosureRow
{
    public Guid LocalOperationId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DestinationCode { get; set; } = string.Empty;
    public string ParcelReadIdsJson { get; set; } = "[]";
    public string SealNumber { get; set; } = string.Empty;
    public string TransportType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? FinalBagBarcode { get; set; }
}

internal sealed class DispatchRow
{
    public Guid LocalOperationId { get; set; }
    public string DispatchId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DestinationCode { get; set; } = string.Empty;
    public string BagBarcodesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public bool CoreConfirmed { get; set; }
}

internal sealed class PendingInboundRow
{
    public Guid OperationId { get; set; }
    public Guid ReadId { get; set; }
    public string PostalTrackingId { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

internal sealed class InboundEvidenceRow
{
    public Guid ReadId { get; set; }
    public string? SupplementaryDataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class InboundImageRow
{
    public Guid ReadId { get; set; }
    public string ImageId { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
    public string? MimeType { get; set; }
    public string? Description { get; set; }
}

internal sealed class ConfigurationRow
{
    public int Id { get; set; }
    public long Version { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
}

internal sealed class DeviceSessionRow
{
    public string ConnectionId { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public string SourceIp { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
    public DateTimeOffset? DisconnectedAt { get; set; }
    public int State { get; set; }
}

internal sealed class DeviceNetworkBindingRow
{
    public string DeviceId { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
}

internal sealed class LocalAdministratorRow
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string NormalizedUserName { get; set; } = string.Empty;
    public string SecretAlgorithm { get; set; } = string.Empty;
    public int SecretIterations { get; set; }
    public string SecretSalt { get; set; } = string.Empty;
    public string SecretHash { get; set; } = string.Empty;
    public Guid CredentialVersion { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class LocalAdministratorSessionRow
{
    public string TokenHash { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public Guid CredentialVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class OperationJournalRow
{
    public Guid OperationId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ResponseJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class ExternalOperationSagaRow
{
    public Guid OperationId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string IntentJson { get; set; } = string.Empty;
    public string? ReceiptJson { get; set; }
    public bool Finalized { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class OutboxRow
{
    public Guid MessageId { get; set; }
    public string Target { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? LastError { get; set; }
}

internal sealed class DeadLetterRow
{
    public Guid MessageId { get; set; }
    public string Target { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset FailedAt { get; set; }
    public string Error { get; set; } = string.Empty;
}
