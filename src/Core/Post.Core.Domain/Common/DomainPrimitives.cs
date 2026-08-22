namespace Post.Core.Domain.Common;

public sealed class DomainRuleException : InvalidOperationException
{
    public DomainRuleException(string message) : base(message)
    {
    }
}

public interface IAggregateRoot;

public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

public enum CoreParcelStatus
{
    Success = 0,
    Error = 1,
    Returned = 2,
    Refused = 3
}

public enum DeviceParcelStatus
{
    Success = 0,
    Pending = 1,
    Error = 2,
    Returned = 3,
    Refused = 4
}

public enum ReadingClassification
{
    Success = 0,
    Returned = 1,
    Refused = 2
}

public enum DeviceActivationStatus
{
    Active = 0,
    Inactive = 1
}

public enum BagState
{
    Closed = 0,
    AssignedToDispatch = 1
}

public enum EdgeConnectionState
{
    Online = 0,
    Offline = 1,
    Disabled = 2
}

public enum OperationalEventType
{
    Connected = 0,
    Disconnected = 1,
    AuthenticationRejected = 2,
    DeviceIpBindingChanged = 3,
    Authenticated = 4
}

public enum OperationalFinalStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2
}

public enum ParcelExportStatus
{
    Unknown = 0,
    NotExported = 1,
    Exported = 2,
    Failed = 3
}

public enum PhysicalAttributeKind
{
    Weight = 0,
    Length = 1,
    Width = 2,
    Height = 3
}

public enum IdempotencyStatus
{
    InProgress = 0,
    Completed = 1,
    Failed = 2
}

public enum OutboxStatus
{
    Pending = 0,
    Published = 1,
    DeadLetter = 2
}

public enum AdminRole
{
    SystemAdministrator = 0,
    GlobalAdministrator = 1,
    CenterAdministrator = 2
}
