using Post.Core.Domain.Common;
using Post.Core.Domain.Devices;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Operations;
using Post.Core.Domain.Security;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Domain.Tests;

public sealed class PlatformAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SecretVerifier Verifier = CreateVerifier(0);

    [Fact]
    public void DeviceLifecyclePreservesCenterAndSnapshotContainsOnlyVerifier()
    {
        var device = SortingDevice.Create(
            DeviceId.New(),
            "Sorter 1",
            "Sorter",
            new LogicalDeviceCode("L1"),
            "Post",
            new ExchangeCenterCode("C1"),
            "Primary",
            Verifier);
        device.UpdateDescriptiveInformation("Sorter 2", "Sorter-X", "Owner", "Updated");
        device.Deactivate();
        device.Activate();
        var replacement = CreateVerifier(1, 220_000);
        device.ReplaceSecretVerifier(replacement);

        var snapshot = ConfigurationSnapshot.Create(
            new ExchangeCenterCode("C1"),
            new ConfigurationVersion(1),
            Now,
            [device],
            [new ExchangeCenterCode("100"), new ExchangeCenterCode("100")],
            OperationalSettings.Default);
        var snapshotDevice = Assert.Single(snapshot.Devices);
        Assert.Equal(replacement.Hash, snapshotDevice.SecretHash);
        Assert.Equal(replacement.Salt, snapshotDevice.SecretSalt);
        Assert.Equal(220_000, snapshotDevice.SecretIterations);
        Assert.Equal("PBKDF2-SHA256", snapshotDevice.SecretAlgorithm);
        Assert.Single(snapshot.PostalCodes);
        Assert.Equal(DeviceActivationStatus.Active, snapshotDevice.ActivationStatus);
    }

    [Fact]
    public void DeviceSecretRotationStagesPreviousVerifierOnlyForBoundedGraceWindow()
    {
        var device = SortingDevice.Create(
            DeviceId.New(),
            "Sorter",
            "Sorter",
            new LogicalDeviceCode("L1"),
            "Post",
            new ExchangeCenterCode("C1"),
            string.Empty,
            Verifier);
        var replacement = CreateVerifier(7);

        device.RotateSecretVerifier(replacement, Now, TimeSpan.FromMinutes(15));

        Assert.Equal(replacement.Hash, device.SecretVerifier.Hash);
        Assert.Equal(Verifier.Hash, device.PreviousSecretVerifier?.Hash);
        Assert.Equal(Now.AddMinutes(15), device.PreviousSecretValidUntil);
        var duringGrace = ConfigurationSnapshot.Create(
            new ExchangeCenterCode("C1"),
            new ConfigurationVersion(1),
            Now.AddMinutes(14),
            [device],
            [],
            OperationalSettings.Default);
        Assert.Equal(Verifier.Hash, Assert.Single(duringGrace.Devices).PreviousSecretHash);
        var afterGrace = ConfigurationSnapshot.Create(
            new ExchangeCenterCode("C1"),
            new ConfigurationVersion(2),
            Now.AddMinutes(15),
            [device],
            [],
            OperationalSettings.Default);
        Assert.Null(Assert.Single(afterGrace.Devices).PreviousSecretHash);
        Assert.Throws<DomainRuleException>(() => device.RotateSecretVerifier(
            Verifier,
            Now,
            SortingDevice.MinimumSecretRotationGrace - TimeSpan.FromTicks(1)));
        Assert.Throws<DomainRuleException>(() => device.RotateSecretVerifier(
            Verifier,
            Now,
            SortingDevice.MaximumSecretRotationGrace + TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void SnapshotRejectsDeviceFromAnotherCenter()
    {
        var device = SortingDevice.Create(
            DeviceId.New(), "D", "T", new LogicalDeviceCode("L"), "O",
            new ExchangeCenterCode("OTHER"), string.Empty, Verifier);
        Assert.Throws<DomainRuleException>(() => ConfigurationSnapshot.Create(
            new ExchangeCenterCode("C1"),
            new ConfigurationVersion(1),
            Now,
            [device],
            [],
            OperationalSettings.Default));
    }

    [Fact]
    public void EdgeCredentialCanRotateDisableAndEnable()
    {
        var credential = EdgeCredential.Provision(
            DeviceId.New(),
            new LogicalDeviceCode("L1"),
            new ExchangeCenterCode("C1"),
            Verifier);
        credential.Disable();
        Assert.False(credential.IsActive);
        credential.Enable();
        var replacement = CreateVerifier(2);
        credential.Rotate(replacement);
        Assert.True(credential.IsActive);
        Assert.Equal(replacement.Hash, credential.SecretVerifier.Hash);
    }

    [Fact]
    public void EdgeHealthHonorsHeartbeatOrderingAndStaleBoundary()
    {
        var zeroVersion = EdgeHealthStatus.Register(
            EdgeId.New(), new ExchangeCenterCode("C1"), Now, "1.0", 0);
        Assert.Equal(0, zeroVersion.ConfigurationVersion);
        Assert.Throws<DomainRuleException>(() => EdgeHealthStatus.Register(
            EdgeId.New(), new ExchangeCenterCode("C1"), Now, "1.0", -1));

        var status = EdgeHealthStatus.Register(
            EdgeId.New(),
            new ExchangeCenterCode("C1"),
            Now,
            "1.0",
            1);
        status.ApplyHeartbeat(Now.AddMinutes(1), "1.1", 2, 10_000, 3, 2, 1, Now);

        Assert.Equal(3, status.PendingCount);
        Assert.Equal(10_000, status.AvailableDiskBytes);
        Assert.False(status.MarkOfflineWhenStale(Now.AddMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.True(status.MarkOfflineWhenStale(Now.AddMinutes(6).AddTicks(1), TimeSpan.FromMinutes(5)));
        Assert.Equal(EdgeConnectionState.Offline, status.ConnectionState);
        Assert.False(status.MarkOfflineWhenStale(Now.AddMinutes(7), TimeSpan.FromMinutes(5)));
        Assert.Throws<DomainRuleException>(() => status.ApplyHeartbeat(
            Now, "1.0", 1, 0, 0, 0, 0, Now));
        Assert.Throws<DomainRuleException>(() => status.ApplyHeartbeat(
            Now.AddMinutes(2), "1.0", 1, -1, 0, 0, 0, Now));
        Assert.Throws<DomainRuleException>(() => status.MarkOfflineWhenStale(Now, TimeSpan.Zero));
        status.Disable();
        Assert.False(status.MarkOfflineWhenStale(Now.AddDays(1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void OperationalResultValidatesTimeAttemptsAndReplay()
    {
        var result = OperationalResult.Create(
            EdgeId.New(),
            new ExchangeCenterCode("C1"),
            new IdempotencyKey("key"),
            "hash",
            new CorrelationId("corr"),
            new ParcelBarcode("P1"),
            "ok",
            true,
            null,
            null,
            Now,
            Now.AddSeconds(1),
            1,
            OperationalFinalStatus.Succeeded);
        Assert.True(result.IsReplay("hash"));
        Assert.False(result.IsReplay("different"));
        Assert.Throws<DomainRuleException>(() => OperationalResult.Create(
            EdgeId.New(), new ExchangeCenterCode("C1"), new IdempotencyKey("x"), "hash",
            new CorrelationId("corr"), null, "x", false, null, null,
            Now, Now.AddTicks(-1), 1, OperationalFinalStatus.Failed));
        Assert.Throws<DomainRuleException>(() => OperationalResult.Create(
            EdgeId.New(), new ExchangeCenterCode("C1"), new IdempotencyKey("x"), "hash",
            new CorrelationId("corr"), null, "x", false, null, null,
            Now, Now, 0, OperationalFinalStatus.Failed));
        Assert.NotEqual(Guid.Empty, result.EdgeId.Value);
        Assert.Equal("C1", result.ExchangeCenterCode.Value);
    }

    [Fact]
    public void OperationalAndAuditEventsCaptureNonSecretMetadata()
    {
        Assert.Equal(4, (int)OperationalEventType.Authenticated);
        var edgeId = EdgeId.New();
        var operationalEvent = OperationalEvent.Create(
            OperationalEventType.Authenticated,
            edgeId,
            null,
            new CorrelationId("corr"),
            Now);
        var audit = AuditRecord.Create("actor", "update", "device", "1", "details", new CorrelationId("corr"), Now);

        Assert.Equal(edgeId, operationalEvent.EdgeId);
        Assert.Equal(OperationalEventType.Authenticated, operationalEvent.EventType);
        Assert.Equal("actor", audit.Actor);
        Assert.NotEqual(Guid.Empty, audit.Id);
    }

    [Fact]
    public void IdempotencyRecordHasStrictPayloadAndTerminalResponseRules()
    {
        var record = IdempotencyRecord.Start(new IdempotencyKey("key"), "hash", Now);
        Assert.True(record.HasSameRequest("hash"));
        record.Complete("response", Now.AddSeconds(1));
        record.Complete("response", Now.AddSeconds(2));
        Assert.Equal(IdempotencyStatus.Completed, record.Status);
        Assert.Throws<DomainRuleException>(() => record.Complete("changed", Now.AddSeconds(3)));
        Assert.Throws<DomainRuleException>(() => record.Fail(Now.AddSeconds(3)));

        var failed = IdempotencyRecord.Start(new IdempotencyKey("failed"), "hash", Now);
        failed.Fail(Now.AddSeconds(1));
        Assert.Equal(IdempotencyStatus.Failed, failed.Status);
    }

    [Fact]
    public void InboxAndOutboxSupportAtLeastOnceProcessingState()
    {
        var eventId = Guid.NewGuid();
        var inbox = InboxMessage.Processed(eventId, "hash", Now);
        Assert.True(inbox.HasSamePayload("hash"));
        Assert.False(inbox.HasSamePayload("different"));

        var outbox = OutboxMessage.Create("topic", "{}", new CorrelationId("corr"), Now, 1);
        outbox.RecordFailure("temporary", 2);
        Assert.Equal(OutboxStatus.Pending, outbox.Status);
        outbox.RecordFailure("again", 2);
        Assert.Equal(OutboxStatus.DeadLetter, outbox.Status);

        var published = OutboxMessage.Create("topic", "{}", new CorrelationId("corr"), Now);
        published.MarkPublished(Now.AddSeconds(1));
        Assert.Equal(OutboxStatus.Published, published.Status);
        Assert.Null(published.LastError);
        Assert.Throws<DomainRuleException>(() => published.RecordFailure("x", 0));
    }

    [Fact]
    public void SecretVerifierAcceptsOnlyCanonicalBoundedPbkdf2Sha256Material()
    {
        var minimum = CreateVerifier(3, SecretVerifier.MinimumIterations);
        var maximum = CreateVerifier(4, SecretVerifier.MaximumIterations);

        Assert.Equal(SecretVerifier.AlgorithmName, minimum.Algorithm);
        Assert.Equal(SecretVerifier.MinimumIterations, minimum.Iterations);
        Assert.Equal(SecretVerifier.MaximumIterations, maximum.Iterations);
        Assert.Equal(SecretVerifier.HashLength, Convert.FromBase64String(minimum.Hash).Length);
        Assert.Equal(SecretVerifier.SaltLength, Convert.FromBase64String(minimum.Salt).Length);

        Assert.Throws<DomainRuleException>(() => new SecretVerifier(
            minimum.Hash,
            minimum.Salt,
            SecretVerifier.MinimumIterations - 1));
        Assert.Throws<DomainRuleException>(() => new SecretVerifier(
            minimum.Hash,
            minimum.Salt,
            SecretVerifier.MaximumIterations + 1));
        Assert.Throws<DomainRuleException>(() => new SecretVerifier(
            minimum.Hash,
            minimum.Salt,
            SecretVerifier.MinimumIterations,
            "PBKDF2-SHA1"));
        Assert.Throws<DomainRuleException>(() => new SecretVerifier(
            minimum.Hash,
            minimum.Salt,
            SecretVerifier.MinimumIterations,
            " "));
        Assert.Throws<DomainRuleException>(() => new SecretVerifier(
            " ",
            minimum.Salt,
            SecretVerifier.MinimumIterations));
        Assert.Throws<DomainRuleException>(() => new SecretVerifier(
            minimum.Hash,
            "not-canonical-base64!!",
            SecretVerifier.MinimumIterations));
        Assert.Throws<DomainRuleException>(() => new SecretVerifier(
            minimum.Hash[..^2] + "B=",
            minimum.Salt,
            SecretVerifier.MinimumIterations));
    }

    private static SecretVerifier CreateVerifier(byte marker, int iterations = 210_000)
    {
        var hash = Enumerable.Repeat(marker, SecretVerifier.HashLength).ToArray();
        var salt = Enumerable.Repeat(marker, SecretVerifier.SaltLength).ToArray();
        return new SecretVerifier(
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt),
            iterations);
    }
}
