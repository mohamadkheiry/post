using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Devices;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Application.Services;

public sealed class ConfigurationSyncService(
    IConfigurationSource source,
    IDeviceConfigurationRepository repository,
    IClock clock) : IConfigurationSyncService
{
    public async Task<bool> SyncAsync(CancellationToken cancellationToken = default)
    {
        var current = await repository.GetAsync(cancellationToken);
        var incoming = await source.FetchAsync(current?.Version, cancellationToken);
        if (incoming is null) return false;
        if (current is not null && incoming.Version <= current.Version) return false;

        var center = PostalCenterCode.Create(incoming.CenterCode);
        var devices = incoming.Devices.Select(x => new DeviceReplica(
            x.DeviceId,
            x.LogicalId,
            new DeviceSecretVerifier(x.SecretAlgorithm, x.SecretIterations, x.SecretSalt, x.SecretHash),
            x.IsActive,
            center,
            PreviousVerifier(x),
            x.PreviousSecretValidUntil));
        var snapshot = new ConfigurationSnapshot(
            center.Value,
            incoming.Version,
            clock.UtcNow,
            devices,
            incoming.SupportedPostalCodes,
            incoming.MinRereadWindow,
            incoming.MaxRereadWindow,
            incoming.AutoDispatchEnabled,
            incoming.MaximumOpenBagMinutes);
        await repository.ReplaceAsync(snapshot, cancellationToken);
        return true;
    }

    private static DeviceSecretVerifier? PreviousVerifier(DeviceConfigurationDto device)
    {
        var values = new object?[]
        {
            device.PreviousSecretAlgorithm,
            device.PreviousSecretIterations,
            device.PreviousSecretSalt,
            device.PreviousSecretHash,
            device.PreviousSecretValidUntil
        };
        if (values.All(value => value is null)) return null;
        if (values.Any(value => value is null))
            throw new InvalidOperationException("A previous device verifier snapshot must be complete.");
        return new DeviceSecretVerifier(
            device.PreviousSecretAlgorithm!,
            device.PreviousSecretIterations!.Value,
            device.PreviousSecretSalt!,
            device.PreviousSecretHash!);
    }
}
