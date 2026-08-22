using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Devices;

namespace Post.Edge.Application.Services;

public sealed class DeviceConnectionService(
    IDeviceConfigurationRepository configurations,
    IDeviceNetworkBindingRepository networkBindings,
    IDeviceNetworkPolicy networkPolicy,
    IDeviceSessionStore sessions,
    IDeviceCredentialVerifier credentialVerifier,
    IOutboxWriter outbox,
    IEdgeUnitOfWork unitOfWork,
    IClock clock) : IDeviceConnectionService
{
    public async Task<Guid> ConnectedAsync(
        DeviceConnectedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var existing = await sessions.FindByConnectionAsync(request.ConnectionId, cancellationToken);
        if (existing is not null)
        {
            return existing.SessionId;
        }

        var session = DeviceSession.Connect(Guid.NewGuid(), request.ConnectionId, request.RemoteIpAddress, clock.UtcNow);
        await sessions.SaveAsync(session, cancellationToken);
        EnqueueEvent("edge.device.connected", null, session);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return session.SessionId;
    }

    public async Task<DeviceAuthResponse> AuthenticateAsync(
        DeviceAuthRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deviceIdIsBounded = !string.IsNullOrWhiteSpace(request.DeviceId) &&
                                request.DeviceId.Length <= DeviceAuthenticationLimits.MaximumDeviceIdCharacters;
        var tokenIsBounded = !string.IsNullOrEmpty(request.Token) &&
                             request.Token.Length <= DeviceAuthenticationLimits.MaximumDeviceTokenCharacters;
        var lookupDeviceId = deviceIdIsBounded ? request.DeviceId : "invalid-device-id";
        var boundedToken = tokenIsBounded ? request.Token : "invalid-device-secret";
        var snapshot = await configurations.GetAsync(cancellationToken);
        var device = snapshot?.Devices.SingleOrDefault(x =>
            string.Equals(x.DeviceId, lookupDeviceId, StringComparison.Ordinal));
        var session = await sessions.FindByConnectionAsync(request.ConnectionId, cancellationToken);
        var bindings = await networkBindings.ListAsync(lookupDeviceId, cancellationToken);
        var ipAllowed = bindings.Count == 0
            ? networkPolicy.AllowDeviceWithoutBinding
            : bindings.Any(x => x.Matches(request.RemoteIpAddress));
        var normalizedRemoteIp = new DeviceNetworkBinding(lookupDeviceId, request.RemoteIpAddress).IpAddress;
        var currentTokenValid = credentialVerifier.Verify(device?.SecretVerifier, boundedToken);
        var previousTokenValid = credentialVerifier.Verify(device?.PreviousSecretVerifier, boundedToken);
        var tokenValid = currentTokenValid || previousTokenValid &&
            device?.PreviousSecretValidUntil is { } graceExpiry && clock.UtcNow <= graceExpiry;

        if (!deviceIdIsBounded || !tokenIsBounded || device is null || !device.Active || session is null ||
            !string.Equals(session.SourceIp, normalizedRemoteIp, StringComparison.OrdinalIgnoreCase) ||
            !ipAllowed ||
            !tokenValid)
        {
            if (session is not null)
            {
                EnqueueEvent("edge.device.authentication-rejected", device?.DeviceId, session);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            return new DeviceAuthResponse(0, null, clock.UtcNow, "AUTH_FAILED", "Device authentication failed.");
        }

        session.Authenticate(device.DeviceId);
        await sessions.SaveAsync(session, cancellationToken);
        EnqueueEvent("edge.device.authenticated", device.DeviceId, session);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DeviceAuthResponse(1, session.SessionId, clock.UtcNow);
    }

    public async Task DisconnectedAsync(
        DeviceDisconnectedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await sessions.FindByConnectionAsync(request.ConnectionId, cancellationToken);
        if (session is null) return;
        session.Disconnect(clock.UtcNow);
        EnqueueEvent("edge.device.disconnected", session.DeviceId, session);
        await sessions.RemoveAsync(request.ConnectionId, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    private void EnqueueEvent(string messageType, string? deviceId, DeviceSession session)
    {
        var suffix = messageType["edge.device.".Length..];
        outbox.Enqueue(ApplicationSupport.Outbox(
            clock,
            "Core",
            messageType,
            new CoreDeviceOperationalEvent(
                deviceId,
                session.SessionId.ToString("D"),
                clock.UtcNow),
            $"device:{suffix}:{session.SessionId:N}"));
    }
}
