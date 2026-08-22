using Post.Edge.Domain.Common;
using Post.Edge.Domain.Devices;
using Post.Edge.Domain.Dispatches;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Domain.Tests;

public sealed class DeviceAndDispatchTests
{
    [Fact]
    public void DeviceReplicaVerifiesPbkdf2SecretWithoutRetainingRawToken()
    {
        var verifier = DeviceSecretVerifier.DeriveForDevelopment("secret");
        var device = new DeviceReplica("D1", "L1", verifier, true, PostalCenterCode.Create("1"));

        Assert.True(device.SecretVerifier.Verify("secret"));
        Assert.False(device.SecretVerifier.Verify("wrong"));
        Assert.False(device.SecretVerifier.Verify(string.Empty));
        Assert.DoesNotContain("secret", device.SecretVerifier.HashBase64, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MD5", 100000, "AAAAAAAAAAAAAAAAAAAAAA==", "AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("PBKDF2-SHA256", 1, "AAAAAAAAAAAAAAAAAAAAAA==", "AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("PBKDF2-SHA256", 99999, "AAAAAAAAAAAAAAAAAAAAAA==", "AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("PBKDF2-SHA256", 2000001, "AAAAAAAAAAAAAAAAAAAAAA==", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("PBKDF2-SHA256", 100000, "bad", "AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("PBKDF2-SHA256", 100000, "AAAAAAAAAAAAAAAAAAAAAAAAAAA=", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("PBKDF2-SHA256", 100000, "AAAAAAAAAAAAAAAAAAAAAA==", "AAAAAAAAAAAAAAAAAAAAAA==")]
    public void SecretVerifierRejectsMalformedConfiguration(
        string algorithm,
        int iterations,
        string salt,
        string hash)
    {
        Assert.Throws<DomainException>(() => new DeviceSecretVerifier(algorithm, iterations, salt, hash));
    }

    [Fact]
    public void SecretVerifierAcceptsOneHundredThousandIterationBoundary()
    {
        var verifier = new DeviceSecretVerifier(
            "PBKDF2-SHA256",
            100_000,
            "AAAAAAAAAAAAAAAAAAAAAA==",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

        Assert.Equal(100_000, verifier.Iterations);
    }

    [Fact]
    public void SecretVerifierAcceptsMaximumIterationAndExactSizeBoundary()
    {
        var verifier = new DeviceSecretVerifier(
            "PBKDF2-SHA256",
            DeviceSecretVerifier.MaximumIterations,
            "AAAAAAAAAAAAAAAAAAAAAA==",
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

        Assert.Equal(DeviceSecretVerifier.MaximumIterations, verifier.Iterations);
        Assert.Equal(DeviceSecretVerifier.SaltSizeBytes, Convert.FromBase64String(verifier.SaltBase64).Length);
        Assert.Equal(DeviceSecretVerifier.HashSizeBytes, Convert.FromBase64String(verifier.HashBase64).Length);
    }

    [Fact]
    public void DeviceNetworkBindingNormalizesIpv4AndRejectsInvalidIp()
    {
        var binding = new DeviceNetworkBinding("D1", "127.0.0.1");

        Assert.True(binding.Matches("::ffff:127.0.0.1"));
        Assert.Throws<DomainException>(() => new DeviceNetworkBinding("D1", "invalid"));
    }

    [Fact]
    public void DeviceSessionConnectsAuthenticatesAndDisconnects()
    {
        var now = DateTimeOffset.UtcNow;
        var session = DeviceSession.Connect(Guid.NewGuid(), "C1", "127.0.0.1", now);

        session.Authenticate("D1");
        session.Disconnect(now.AddSeconds(1));
        session.Disconnect(now.AddSeconds(2));

        Assert.Equal(DeviceSessionState.Disconnected, session.State);
        Assert.Equal("D1", session.DeviceId);
        Assert.Throws<DomainException>(() => session.Authenticate("D1"));
    }

    [Fact]
    public void ConfigurationSnapshotNormalizesAndDeduplicatesPostalCodes()
    {
        var center = PostalCenterCode.Create("1");
        var snapshot = new ConfigurationSnapshot(
            "1", 2, DateTimeOffset.UtcNow,
            [new DeviceReplica("D1", "L1", DeviceSecretVerifier.DeriveForDevelopment("T"), true, center)],
            ["2", "00002"],
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));

        Assert.Equal("00001", snapshot.ExchangeCenterCode);
        Assert.Single(snapshot.PostalCodes);
        Assert.Throws<DomainException>(() => new ConfigurationSnapshot(
            "1", 1, DateTimeOffset.UtcNow, [], [], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void DispatchRequiresBagsAndMarksCoreConfirmation()
    {
        var dispatch = DispatchRecord.Create(
            Guid.NewGuid(), "DSP-1", "D1", "00002", ["B1", "B1", "B2"], DateTimeOffset.UtcNow);

        dispatch.MarkCoreConfirmed();

        Assert.Equal(2, dispatch.BagBarcodes.Count);
        Assert.True(dispatch.CoreConfirmed);
        Assert.Throws<DomainException>(() => DispatchRecord.Create(
            Guid.NewGuid(), "DSP-2", "D1", "00002", [], DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DispatchRehydrateRestoresPersistedConfirmationAndNormalizesValues()
    {
        var operationId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var dispatch = DispatchRecord.Rehydrate(
            operationId,
            " DSP-1 ",
            " D1 ",
            " 00002 ",
            ["B1", "B1", "B2"],
            createdAt,
            coreConfirmed: true);

        Assert.Equal(operationId, dispatch.LocalOperationId);
        Assert.Equal("DSP-1", dispatch.DispatchId);
        Assert.Equal("D1", dispatch.DeviceId);
        Assert.Equal("00002", dispatch.DestinationCenterCode);
        Assert.Equal(["B1", "B2"], dispatch.BagBarcodes);
        Assert.Equal(createdAt, dispatch.CreatedAt);
        Assert.True(dispatch.CoreConfirmed);
    }
}
