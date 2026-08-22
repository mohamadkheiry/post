using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Devices;

namespace Post.Edge.Application.Services;

public sealed class DeviceCredentialVerifier : IDeviceCredentialVerifier
{
    private const string InvalidSecretSurrogate = "invalid-device-secret";
    private static readonly DeviceSecretVerifier DummyVerifier = new(
        "PBKDF2-SHA256",
        100_000,
        "AAAAAAAAAAAAAAAAAAAAAA==",
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

    public bool Verify(DeviceSecretVerifier? configuredVerifier, string suppliedSecret)
    {
        var inputIsBounded = !string.IsNullOrEmpty(suppliedSecret) &&
                             suppliedSecret.Length <= DeviceAuthenticationLimits.MaximumDeviceTokenCharacters;
        var boundedSecret = inputIsBounded ? suppliedSecret : InvalidSecretSurrogate;
        var verified = (configuredVerifier ?? DummyVerifier).Verify(boundedSecret);
        return verified & inputIsBounded;
    }
}
