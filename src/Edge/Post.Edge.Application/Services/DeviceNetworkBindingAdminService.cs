using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Devices;

namespace Post.Edge.Application.Services;

public sealed class DeviceNetworkBindingAdminService(
    IDeviceNetworkBindingRepository bindings,
    IDeviceConfigurationRepository configurations,
    IOutboxWriter outbox,
    IEdgeUnitOfWork unitOfWork,
    IClock clock) : IDeviceNetworkBindingAdminService
{
    public async Task<ReplaceDeviceNetworkBindingsResult> ReplaceAsync(
        ReplaceDeviceNetworkBindingsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Actor) || command.Actor.Length > 256 ||
            command.Actor.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Audit actor is invalid.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.CorrelationId) || command.CorrelationId.Length > 128 ||
            command.CorrelationId.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("Audit correlation id is invalid.", nameof(command));

        var configuration = await configurations.GetAsync(cancellationToken)
                            ?? throw new InvalidOperationException("Device configuration is unavailable.");
        var device = configuration.Devices.SingleOrDefault(candidate =>
            string.Equals(candidate.DeviceId, command.DeviceId, StringComparison.Ordinal));
        if (device is null)
            throw new ArgumentException("The device does not exist in the current configuration.", nameof(command));

        var normalized = command.IpAddresses
            .Select(ipAddress => new DeviceNetworkBinding(command.DeviceId, ipAddress))
            .DistinctBy(binding => binding.IpAddress, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await bindings.ReplaceAsync(command.DeviceId, normalized.Select(binding => binding.IpAddress).ToArray(), cancellationToken);
        var auditId = Guid.NewGuid();
        outbox.Enqueue(ApplicationSupport.Outbox(
            clock,
            "Core",
            "edge.device-ip-binding.changed",
            new
            {
                DeviceId = command.DeviceId,
                BindingCount = normalized.Length,
                Actor = command.Actor,
                CorrelationId = command.CorrelationId,
                OccurredAt = clock.UtcNow
            },
            $"device-ip-binding:{auditId:N}"));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new ReplaceDeviceNetworkBindingsResult(
            command.DeviceId,
            normalized.Select(binding => binding.IpAddress).Order(StringComparer.Ordinal).ToArray());
    }
}
