using Microsoft.EntityFrameworkCore;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Application.Devices;
using Post.Core.Application.Security;
using Post.Core.Domain.Common;
using Post.Core.Domain.Devices;
using Post.Core.Infrastructure.Persistence;
using Post.Core.Infrastructure.Security;
using Post.Core.Infrastructure.Testing;

namespace Post.Core.Application.Tests;

public sealed class DeviceAndSecurityServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeviceRegistrationReturnsRawSecretOnceAndSnapshotContainsOnlyVerifier()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfDeviceConfigStore(database.DeviceConfig);
        var hasher = new Pbkdf2SecretHasher();
        var service = new DeviceManagementService(
            store,
            hasher,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now));
        var command = new RegisterDeviceCommand(
            "Sorter", "Sorter", "L1", "Post", "C1", "primary", "register-one", "admin", "corr");

        var registered = await service.RegisterAsync(command, cancellationToken: TestContext.Current.CancellationToken);
        var registrationReplay = await service.RegisterAsync(command, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(registrationReplay.IsReplay);
        Assert.True(registrationReplay.RecoveryRequired);
        Assert.Null(registrationReplay.DeviceSecret);
        await Assert.ThrowsAsync<CoreConflictException>(() => service.RegisterAsync(
            command with { Name = "Changed" }, cancellationToken: TestContext.Current.CancellationToken));
        var persisted = await store.FindDeviceAsync(new Post.Core.Domain.ValueObjects.DeviceId(registered.DeviceId), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.True(hasher.Verify(registered.DeviceSecret!, persisted.SecretVerifier));
        Assert.NotEqual(registered.DeviceSecret, persisted.SecretVerifier.Hash);
        var initialVerifierHash = persisted.SecretVerifier.Hash;

        var configuration = new ConfigurationService(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now));
        var snapshot = await configuration.PublishAsync(new PublishConfigurationCommand(
            "C1", ["100"], TimeSpan.FromHours(6), TimeSpan.FromHours(72), 1, 2, "config-one", "admin", "corr"), cancellationToken: TestContext.Current.CancellationToken);
        var device = Assert.Single(snapshot.Devices);
        Assert.Equal(Pbkdf2SecretHasher.AlgorithmName, device.SecretAlgorithm);
        Assert.Equal(initialVerifierHash, device.SecretHash);
        Assert.DoesNotContain(registered.DeviceSecret!, device.SecretHash, StringComparison.Ordinal);
        var bootstrapped = await configuration.GetBootstrapAsync("C1", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(bootstrapped);
        Assert.Equal(snapshot.ExchangeCenterCode, bootstrapped.ExchangeCenterCode);
        Assert.Equal(snapshot.Version, bootstrapped.Version);
        Assert.Equal(snapshot.Devices.ToArray(), bootstrapped.Devices.ToArray());

        var rotated = await service.RotateSecretAsync(new RotateDeviceSecretCommand(
            registered.DeviceId, "rotate-one", "admin", "corr"), cancellationToken: TestContext.Current.CancellationToken);
        var rotationReplay = await service.RotateSecretAsync(new RotateDeviceSecretCommand(
            registered.DeviceId, "rotate-one", "admin", "another-correlation"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(rotationReplay.IsReplay);
        Assert.True(rotationReplay.RecoveryRequired);
        Assert.Null(rotationReplay.DeviceSecret);
        var afterRotation = await store.FindDeviceAsync(
            new Post.Core.Domain.ValueObjects.DeviceId(registered.DeviceId), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(afterRotation);
        Assert.True(hasher.Verify(registered.DeviceSecret!, afterRotation.PreviousSecretVerifier!));
        Assert.True(hasher.Verify(rotated.DeviceSecret!, afterRotation.SecretVerifier));
        Assert.Equal(Now.Add(SortingDevice.DefaultSecretRotationGrace), afterRotation.PreviousSecretValidUntil);
        var rotatedSnapshot = await configuration.GetBootstrapAsync("C1", cancellationToken: TestContext.Current.CancellationToken);
        var rotatedDevice = Assert.Single(rotatedSnapshot.Devices);
        Assert.Equal(initialVerifierHash, rotatedDevice.PreviousSecretHash);
        Assert.Equal(Now.Add(SortingDevice.DefaultSecretRotationGrace), rotatedDevice.PreviousSecretValidUntil);
        Assert.DoesNotContain(database.DeviceConfig.IdempotencyRecords,
            x => x.ResponseSnapshot.Contains(rotated.DeviceSecret!, StringComparison.Ordinal));
        Assert.DoesNotContain(database.DeviceConfig.OutboxMessages,
            x => x.Payload.Contains(rotated.DeviceSecret!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeviceAdminQueriesRespectScopeAndActivationPublishesNewSnapshot()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfDeviceConfigStore(database.DeviceConfig);
        var time = new MutableTimeProvider(Now);
        var management = new DeviceManagementService(
            store,
            new Pbkdf2SecretHasher(),
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            time);
        var registered = await management.RegisterAsync(new RegisterDeviceCommand(
            "Sorter", "Sorter", "L1", "Post", "C1", null), cancellationToken: TestContext.Current.CancellationToken);
        var configuration = new ConfigurationService(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            time);
        var published = await configuration.PublishAsync(new PublishConfigurationCommand(
            "C1", ["100"], TimeSpan.FromHours(6), TimeSpan.FromHours(72), 1, 2, "config-one", "admin", "corr",
            AutoDispatchEnabled: true,
            MaximumOpenBagDuration: TimeSpan.FromMinutes(90)), cancellationToken: TestContext.Current.CancellationToken);
        var publishReplay = await configuration.PublishAsync(new PublishConfigurationCommand(
            "C1", ["100"], TimeSpan.FromHours(6), TimeSpan.FromHours(72), 1, 2, "config-one", "admin", "retry-corr",
            AutoDispatchEnabled: true,
            MaximumOpenBagDuration: TimeSpan.FromMinutes(90)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(published.Version, publishReplay.Version);
        Assert.True(published.AutoDispatchEnabled);
        Assert.Equal(90, published.MaximumOpenBagMinutes);
        await Assert.ThrowsAsync<CoreConflictException>(() => configuration.PublishAsync(new PublishConfigurationCommand(
            "C1", ["100"], TimeSpan.FromHours(6), TimeSpan.FromHours(72), 2, 2, "config-one", "admin", "corr"), cancellationToken: TestContext.Current.CancellationToken));

        var update = new UpdateDeviceCommand(
            registered.DeviceId, "Updated", "Sorter-X", "Owner", "description", "update-one", "admin", "corr");
        await management.UpdateAsync(update, cancellationToken: TestContext.Current.CancellationToken);
        await management.UpdateAsync(update with { CorrelationId = "retry-corr" }, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<CoreConflictException>(() => management.UpdateAsync(
            update with { Name = "Different" }, cancellationToken: TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromMinutes(1));
        await management.SetActivationAsync(registered.DeviceId, false, "activation-one", "admin", "corr", cancellationToken: TestContext.Current.CancellationToken);
        await management.SetActivationAsync(registered.DeviceId, false, "activation-one", "admin", "retry-corr", cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<CoreConflictException>(() => management.SetActivationAsync(
            registered.DeviceId, true, "activation-one", "admin", "corr", cancellationToken: TestContext.Current.CancellationToken));

        var query = new DeviceQueryService(store);
        var device = Assert.Single(await query.ListAsync(new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("Updated", device.Name);
        Assert.Equal(DeviceActivationStatus.Inactive, device.ActivationStatus);
        Assert.Empty(await query.ListAsync(new AccessScope(false, ["NO"]), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(await query.ListAsync(AccessScope.Global, "C1", cancellationToken: TestContext.Current.CancellationToken));

        var configurationQuery = new ConfigurationQueryService(store);
        var current = await configurationQuery.GetCurrentAsync("C1", new AccessScope(false, ["C1"]), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, current?.Version);
        Assert.Null(await configurationQuery.GetCurrentAsync("C1", new AccessScope(false, ["NO"]), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitialAdminProvisioningBuildsAtomicIdentityRequestAndIssuesClaims()
    {
        var authority = new RecordingAdminIdentityAuthority(
            new AdminIdentityProfile(
                Guid.NewGuid(),
                "admin",
                true,
                [AdminRole.GlobalAdministrator],
                ["C1"]),
            "test-correct-horse-battery-staple")
        {
            RejectSubsequentProvisioning = true
        };
        var provisioning = new AdminProvisioningService(authority, new MutableTimeProvider(Now));
        var command = new ProvisionInitialAdminCommand(
            "admin",
            "test-correct-horse-battery-staple",
            [AdminRole.GlobalAdministrator],
            ["C1", "C1"],
            "corr");

        var result = await provisioning.ProvisionInitialAsync(command, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("admin", result.UserName);
        await Assert.ThrowsAsync<CoreConflictException>(() => provisioning.ProvisionInitialAsync(command, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(result.UserId, authority.ProvisionedIdentity?.UserId);
        Assert.Equal([AdminRole.GlobalAdministrator], authority.ProvisionedIdentity?.Roles);
        Assert.Equal(["C1"], authority.ProvisionedIdentity?.AllowedCenterCodes);
        Assert.Equal("admin.initial-provision", authority.ProvisionedAudit?.Action);
        Assert.Equal("identity.admin-provisioned", authority.ProvisionedOutbox?.Topic);
        Assert.DoesNotContain(command.Password, authority.ProvisionedAudit?.Details ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(command.Password, authority.ProvisionedOutbox?.Payload ?? string.Empty, StringComparison.Ordinal);

        var tokenIssuer = new RecordingTokenIssuer();
        var auth = new AdminAuthService(authority, tokenIssuer, new MutableTimeProvider(Now));
        var token = await auth.AuthenticateAsync(new AuthenticateAdminCommand(
            "admin", "test-correct-horse-battery-staple", TimeSpan.FromHours(1)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("token", token.Token);
        Assert.Contains(AdminRole.GlobalAdministrator.ToString(), tokenIssuer.Descriptor!.Roles);
        Assert.Contains("C1", tokenIssuer.Descriptor.AllowedCenters);
        await Assert.ThrowsAsync<CoreAuthorizationException>(() => auth.AuthenticateAsync(new AuthenticateAdminCommand(
            "admin", "wrong", TimeSpan.FromHours(1)), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InitialAdminRequiresPrivilegedRoleAndStrongPassword()
    {
        var authority = new RecordingAdminIdentityAuthority();
        var service = new AdminProvisioningService(authority, new MutableTimeProvider(Now));
        await Assert.ThrowsAsync<CoreValidationException>(() => service.ProvisionInitialAsync(
            new ProvisionInitialAdminCommand("admin", "short", [AdminRole.GlobalAdministrator], [], "corr"), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreValidationException>(() => service.ProvisionInitialAsync(
            new ProvisionInitialAdminCommand(
                "admin", null!, [AdminRole.GlobalAdministrator], [], "corr"), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreValidationException>(() => service.ProvisionInitialAsync(
            new ProvisionInitialAdminCommand(
                "admin", "test-correct-horse-battery-staple", [AdminRole.CenterAdministrator], ["C1"], "corr"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, authority.ProvisionCalls);
    }

    [Fact]
    public async Task MissingAdministrativeAndEdgeCredentialsUseDummyVerifiersBeforeRejection()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfIdentityStore(database.Identity);
        var tokenIssuer = new RecordingTokenIssuer();
        var edgeHasher = new RecordingSecretHasher(new Pbkdf2SecretHasher());
        var edgeAuthentication = new EdgeAuthService(
            store,
            edgeHasher,
            tokenIssuer,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now));

        await Assert.ThrowsAsync<CoreAuthorizationException>(() => edgeAuthentication.AuthenticateAsync(
            new AuthenticateEdgeCommand("MISSING", "candidate-secret", TimeSpan.FromMinutes(15)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, edgeHasher.VerifyCalls);
        Assert.Same(edgeHasher.DummyVerifier, edgeHasher.LastVerifier);

        var adminAuthority = new RecordingAdminIdentityAuthority();
        var adminAuthentication = new AdminAuthService(
            adminAuthority,
            tokenIssuer,
            new MutableTimeProvider(Now));

        await Assert.ThrowsAsync<CoreAuthorizationException>(() => adminAuthentication.AuthenticateAsync(
            new AuthenticateAdminCommand("missing-user", "candidate-password", TimeSpan.FromMinutes(15)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, adminAuthority.AuthenticationCalls);
        Assert.Equal("missing-user", adminAuthority.LastUserName);
        Assert.Equal("candidate-password", adminAuthority.LastPassword);
    }

    [Fact]
    public async Task OversizedAuthenticationInputsAreRejectedBeforeCredentialHashers()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfIdentityStore(database.Identity);
        var tokenIssuer = new RecordingTokenIssuer();
        var edgeHasher = new RecordingSecretHasher(new Pbkdf2SecretHasher());
        var edgeAuthentication = new EdgeAuthService(
            store,
            edgeHasher,
            tokenIssuer,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now));

        await Assert.ThrowsAsync<CoreValidationException>(() => edgeAuthentication.AuthenticateAsync(
            new AuthenticateEdgeCommand(
                new string('d', AuthenticationInputLimits.LogicalDeviceCodeMaximumLength + 1),
                "candidate-secret",
                TimeSpan.FromMinutes(15)), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreValidationException>(() => edgeAuthentication.AuthenticateAsync(
            new AuthenticateEdgeCommand(
                "EDGE-1",
                new string('s', AuthenticationInputLimits.DeviceSecretMaximumLength + 1),
                TimeSpan.FromMinutes(15)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, edgeHasher.VerifyCalls);
        await Assert.ThrowsAsync<CoreAuthorizationException>(() => edgeAuthentication.AuthenticateAsync(
            new AuthenticateEdgeCommand(
                new string('d', AuthenticationInputLimits.LogicalDeviceCodeMaximumLength),
                new string('s', AuthenticationInputLimits.DeviceSecretMaximumLength),
                TimeSpan.FromMinutes(15)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, edgeHasher.VerifyCalls);

        var adminAuthority = new RecordingAdminIdentityAuthority();
        var adminAuthentication = new AdminAuthService(
            adminAuthority,
            tokenIssuer,
            new MutableTimeProvider(Now));
        await Assert.ThrowsAsync<CoreValidationException>(() => adminAuthentication.AuthenticateAsync(
            new AuthenticateAdminCommand(
                new string('u', AuthenticationInputLimits.UserNameMaximumLength + 1),
                "candidate-password",
                TimeSpan.FromMinutes(15)), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreValidationException>(() => adminAuthentication.AuthenticateAsync(
            new AuthenticateAdminCommand(
                "admin",
                new string('p', AuthenticationInputLimits.PasswordMaximumLength + 1),
                TimeSpan.FromMinutes(15)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, adminAuthority.AuthenticationCalls);
        await Assert.ThrowsAsync<CoreAuthorizationException>(() => adminAuthentication.AuthenticateAsync(
            new AuthenticateAdminCommand(
                new string('u', AuthenticationInputLimits.UserNameMaximumLength),
                new string('p', AuthenticationInputLimits.PasswordMaximumLength),
                TimeSpan.FromMinutes(15)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, adminAuthority.AuthenticationCalls);

        var provisioning = new AdminProvisioningService(
            adminAuthority,
            new MutableTimeProvider(Now));
        await Assert.ThrowsAsync<CoreValidationException>(() => provisioning.ProvisionInitialAsync(
            new ProvisionInitialAdminCommand(
                "admin",
                new string('p', AuthenticationInputLimits.PasswordMaximumLength + 1),
                [AdminRole.GlobalAdministrator],
                [],
                "corr"), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, adminAuthority.ProvisionCalls);
    }

    [Fact]
    public async Task EdgeCredentialProvisionAuthenticateDisableAndAuditArePersistent()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfIdentityStore(database.Identity);
        var hasher = new Pbkdf2SecretHasher();
        var tokenIssuer = new RecordingTokenIssuer();
        var service = new EdgeAuthService(
            store,
            hasher,
            tokenIssuer,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            new MutableTimeProvider(Now));
        var deviceId = Guid.NewGuid();
        var provision = new ProvisionEdgeCredentialCommand(
            deviceId, "L1", "C1", "device-secret", "admin", "corr", "provision-one");

        var firstProvision = await service.ProvisionAsync(provision, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("device-secret", firstProvision.DeviceSecret);
        var replayProvision = await service.ProvisionAsync(provision with { DeviceSecret = "newly-generated-but-discarded" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(replayProvision.IsReplay);
        Assert.True(replayProvision.RecoveryRequired);
        Assert.Null(replayProvision.DeviceSecret);
        await Assert.ThrowsAsync<CoreConflictException>(() => service.ProvisionAsync(
            provision with { LogicalDeviceCode = "L2" }, cancellationToken: TestContext.Current.CancellationToken));
        var token = await service.AuthenticateAsync(new AuthenticateEdgeCommand("L1", "device-secret", TimeSpan.FromHours(1)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("token", token.Token);
        Assert.Contains("Edge", tokenIssuer.Descriptor!.Roles);
        Assert.Contains("C1", tokenIssuer.Descriptor.AllowedCenters);

        var rotated = await service.RotateSecretAsync(new RotateEdgeCredentialCommand(
            deviceId, "replacement-secret", "rotate-one", "admin", "corr"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("replacement-secret", rotated.DeviceSecret);
        var rotateReplay = await service.RotateSecretAsync(new RotateEdgeCredentialCommand(
            deviceId, "another-generated-secret", "rotate-one", "admin", "retry-corr"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(rotateReplay.IsReplay);
        Assert.Null(rotateReplay.DeviceSecret);
        await Assert.ThrowsAsync<CoreAuthorizationException>(() => service.AuthenticateAsync(
            new AuthenticateEdgeCommand("L1", "device-secret", TimeSpan.FromHours(1)), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("token", (await service.AuthenticateAsync(new AuthenticateEdgeCommand(
            "L1", "replacement-secret", TimeSpan.FromHours(1)), cancellationToken: TestContext.Current.CancellationToken)).Token);

        await service.DisableAsync(deviceId, "disable-one", "admin", "corr", cancellationToken: TestContext.Current.CancellationToken);
        await service.DisableAsync(deviceId, "disable-one", "admin", "retry-corr", cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<CoreAuthorizationException>(() => service.AuthenticateAsync(
            new AuthenticateEdgeCommand("L1", "replacement-secret", TimeSpan.FromHours(1)), cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain(database.Identity.IdempotencyRecords,
            x => x.ResponseSnapshot.Contains("device-secret", StringComparison.Ordinal) ||
                 x.ResponseSnapshot.Contains("replacement-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(database.Identity.OutboxMessages,
            x => x.Payload.Contains("device-secret", StringComparison.Ordinal) ||
                 x.Payload.Contains("replacement-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(database.Identity.AuditRecords,
            x => x.Details.Contains("device-secret", StringComparison.Ordinal) ||
                 x.Details.Contains("replacement-secret", StringComparison.Ordinal));

        var audit = new AuditService(store, new MutableTimeProvider(Now));
        await audit.RecordAsync(new AuditCommand("admin", "read", "parcel", "P1", null, "corr"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(4, await database.Identity.AuditRecords.CountAsync(TestContext.Current.CancellationToken));
    }
}
