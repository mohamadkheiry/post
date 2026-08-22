using Post.Core.Domain.Common;
using Post.Core.Domain.Devices;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Domain.Security;

public sealed class EdgeCredential : IAggregateRoot
{
    private EdgeCredential()
    {
        SecretVerifier = new SecretVerifier(
            Convert.ToBase64String(new byte[SecretVerifier.HashLength]),
            Convert.ToBase64String(new byte[SecretVerifier.SaltLength]),
            SecretVerifier.MinimumIterations);
    }

    private EdgeCredential(
        DeviceId deviceId,
        LogicalDeviceCode logicalDeviceCode,
        ExchangeCenterCode exchangeCenterCode,
        SecretVerifier secretVerifier)
    {
        DeviceId = deviceId;
        LogicalDeviceCode = logicalDeviceCode;
        ExchangeCenterCode = exchangeCenterCode;
        SecretVerifier = secretVerifier ?? throw new ArgumentNullException(nameof(secretVerifier));
        IsActive = true;
    }

    public DeviceId DeviceId { get; private set; }

    public LogicalDeviceCode LogicalDeviceCode { get; private set; }

    public ExchangeCenterCode ExchangeCenterCode { get; private set; }

    public SecretVerifier SecretVerifier { get; private set; }

    public bool IsActive { get; private set; }

    public static EdgeCredential Provision(
        DeviceId deviceId,
        LogicalDeviceCode logicalDeviceCode,
        ExchangeCenterCode exchangeCenterCode,
        SecretVerifier secretVerifier) =>
        new(deviceId, logicalDeviceCode, exchangeCenterCode, secretVerifier);

    public void Rotate(SecretVerifier secretVerifier) =>
        SecretVerifier = secretVerifier ?? throw new ArgumentNullException(nameof(secretVerifier));

    public void Disable() => IsActive = false;

    public void Enable() => IsActive = true;
}

public sealed class AuditRecord
{
    private AuditRecord()
    {
        Actor = string.Empty;
        Action = string.Empty;
        ResourceType = string.Empty;
        ResourceId = string.Empty;
        Details = string.Empty;
    }

    private AuditRecord(
        Guid id,
        string actor,
        string action,
        string resourceType,
        string resourceId,
        string? details,
        CorrelationId correlationId,
        DateTimeOffset occurredAt)
    {
        Id = id;
        Actor = Required(actor, nameof(actor), 256);
        Action = Required(action, nameof(action), 128);
        ResourceType = Required(resourceType, nameof(resourceType), 128);
        ResourceId = Required(resourceId, nameof(resourceId), 256);
        Details = details?.Trim() ?? string.Empty;
        if (Details.Length > 4096)
        {
            throw new DomainRuleException("Audit details cannot exceed 4096 characters.");
        }

        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; private set; }

    public string Actor { get; private set; }

    public string Action { get; private set; }

    public string ResourceType { get; private set; }

    public string ResourceId { get; private set; }

    public string Details { get; private set; }

    public CorrelationId CorrelationId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static AuditRecord Create(
        string actor,
        string action,
        string resourceType,
        string resourceId,
        string? details,
        CorrelationId correlationId,
        DateTimeOffset occurredAt) =>
        new(Guid.NewGuid(), actor, action, resourceType, resourceId, details, correlationId, occurredAt);

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }
}
