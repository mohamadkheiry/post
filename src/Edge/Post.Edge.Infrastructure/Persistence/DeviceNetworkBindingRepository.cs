using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Devices;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Persistence;

internal sealed class DeviceNetworkBindingRepository(EdgeDbContext db) : IDeviceNetworkBindingRepository
{
    public async Task<IReadOnlyList<DeviceNetworkBinding>> ListAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        var rows = await db.DeviceNetworkBindings.AsNoTracking()
            .Where(x => x.DeviceId == deviceId)
            .ToListAsync(cancellationToken);
        return rows.Select(x => new DeviceNetworkBinding(x.DeviceId, x.IpAddress)).ToArray();
    }

    public async Task ReplaceAsync(
        string deviceId,
        IReadOnlyCollection<string> ipAddresses,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Device id is required.", nameof(deviceId));
        ArgumentNullException.ThrowIfNull(ipAddresses);
        var bindings = ipAddresses.Select(x => new DeviceNetworkBinding(deviceId, x)).ToArray();
        var existing = await db.DeviceNetworkBindings.Where(x => x.DeviceId == deviceId).ToListAsync(cancellationToken);
        db.DeviceNetworkBindings.RemoveRange(existing);
        db.DeviceNetworkBindings.AddRange(bindings
            .DistinctBy(x => x.IpAddress, StringComparer.OrdinalIgnoreCase)
            .Select(x => new DeviceNetworkBindingRow { DeviceId = x.DeviceId, IpAddress = x.IpAddress }));
    }
}

internal sealed class DeviceNetworkPolicy(
    IOptions<EdgeSecurityOptions> options,
    bool isDevelopmentProfile) : IDeviceNetworkPolicy
{
    public bool AllowDeviceWithoutBinding =>
        isDevelopmentProfile &&
        options.Value.AllowUnboundDevices;
}
