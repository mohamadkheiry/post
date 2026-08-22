using Microsoft.EntityFrameworkCore;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Devices;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Infrastructure.Persistence;

internal sealed record StoredConfiguration(
    string CenterCode,
    long Version,
    DateTimeOffset GeneratedAt,
    StoredDevice[] Devices,
    string[] PostalCodes,
    long ShortRereadWindowTicks,
    long ReturnWindowTicks,
    bool AutoDispatchEnabled,
    int MaximumOpenBagMinutes);

internal sealed record StoredDevice(
    string DeviceId,
    string LogicalCode,
    bool Active,
    string SecretAlgorithm,
    int SecretIterations,
    string SecretSalt,
    string SecretHash,
    string? PreviousSecretAlgorithm,
    int? PreviousSecretIterations,
    string? PreviousSecretSalt,
    string? PreviousSecretHash,
    DateTimeOffset? PreviousSecretValidUntil);

internal sealed class DeviceConfigurationRepository(EdgeDbContext db) : IDeviceConfigurationRepository
{
    public async Task<ConfigurationSnapshot?> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.Configurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (row is null) return null;
        var data = PersistenceJson.Deserialize<StoredConfiguration>(row.PayloadJson);
        var center = PostalCenterCode.Create(data.CenterCode);
        return new ConfigurationSnapshot(
            data.CenterCode,
            data.Version,
            data.GeneratedAt,
            data.Devices.Select(x => new DeviceReplica(
                x.DeviceId,
                x.LogicalCode,
                new DeviceSecretVerifier(x.SecretAlgorithm, x.SecretIterations, x.SecretSalt, x.SecretHash),
                x.Active,
                center,
                PreviousVerifier(x),
                x.PreviousSecretValidUntil)),
            data.PostalCodes,
            TimeSpan.FromTicks(data.ShortRereadWindowTicks),
            TimeSpan.FromTicks(data.ReturnWindowTicks),
            data.AutoDispatchEnabled,
            data.MaximumOpenBagMinutes <= 0 ? 60 : data.MaximumOpenBagMinutes);
    }

    public async Task ReplaceAsync(ConfigurationSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var data = new StoredConfiguration(
            snapshot.ExchangeCenterCode,
            snapshot.Version,
            snapshot.GeneratedAt,
            snapshot.Devices.Select(x => new StoredDevice(
                x.DeviceId,
                x.LogicalCode,
                x.Active,
                x.SecretVerifier.Algorithm,
                x.SecretVerifier.Iterations,
                x.SecretVerifier.SaltBase64,
                x.SecretVerifier.HashBase64,
                x.PreviousSecretVerifier?.Algorithm,
                x.PreviousSecretVerifier?.Iterations,
                x.PreviousSecretVerifier?.SaltBase64,
                x.PreviousSecretVerifier?.HashBase64,
                x.PreviousSecretValidUntil)).ToArray(),
            snapshot.PostalCodes.ToArray(),
            snapshot.ShortRereadWindow.Ticks,
            snapshot.ReturnWindow.Ticks,
            snapshot.AutoDispatchEnabled,
            snapshot.MaximumOpenBagMinutes);
        var row = await db.Configurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (row is null)
        {
            db.Configurations.Add(new ConfigurationRow { Id = 1, Version = snapshot.Version, PayloadJson = PersistenceJson.Serialize(data) });
        }
        else
        {
            row.Version = snapshot.Version;
            row.PayloadJson = PersistenceJson.Serialize(data);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static DeviceSecretVerifier? PreviousVerifier(StoredDevice device) =>
        device.PreviousSecretAlgorithm is null ||
        device.PreviousSecretIterations is null ||
        device.PreviousSecretSalt is null ||
        device.PreviousSecretHash is null
            ? null
            : new DeviceSecretVerifier(
                device.PreviousSecretAlgorithm,
                device.PreviousSecretIterations.Value,
                device.PreviousSecretSalt,
                device.PreviousSecretHash);
}

internal sealed class DeviceSessionStore(EdgeDbContext db) : IDeviceSessionStore
{
    public async Task SaveAsync(DeviceSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var row = await db.DeviceSessions.SingleOrDefaultAsync(x => x.ConnectionId == session.ConnectionId, cancellationToken);
        if (row is null)
        {
            row = new DeviceSessionRow { ConnectionId = session.ConnectionId };
            db.DeviceSessions.Add(row);
        }
        Copy(session, row);
    }

    public async Task<DeviceSession?> FindByConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        var row = await db.DeviceSessions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connectionId, cancellationToken);
        if (row is null) return null;
        var session = DeviceSession.Connect(row.SessionId, row.ConnectionId, row.SourceIp, row.ConnectedAt);
        if ((DeviceSessionState)row.State == DeviceSessionState.Authenticated) session.Authenticate(row.DeviceId!);
        if ((DeviceSessionState)row.State == DeviceSessionState.Disconnected) session.Disconnect(row.DisconnectedAt!.Value);
        return session;
    }

    public async Task RemoveAsync(string connectionId, CancellationToken cancellationToken)
    {
        var row = await db.DeviceSessions.SingleOrDefaultAsync(x => x.ConnectionId == connectionId, cancellationToken);
        if (row is null) return;
        db.DeviceSessions.Remove(row);
    }

    private static void Copy(DeviceSession session, DeviceSessionRow row)
    {
        row.SessionId = session.SessionId;
        row.SourceIp = session.SourceIp;
        row.DeviceId = session.DeviceId;
        row.ConnectedAt = session.ConnectedAt;
        row.DisconnectedAt = session.DisconnectedAt;
        row.State = (int)session.State;
    }
}
