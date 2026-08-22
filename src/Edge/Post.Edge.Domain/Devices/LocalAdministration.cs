using Post.Edge.Domain.Common;

namespace Post.Edge.Domain.Devices;

public sealed record LocalAdministrator
{
    public LocalAdministrator(
        string userName,
        DeviceSecretVerifier passwordVerifier,
        Guid credentialVersion,
        DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 128)
            throw new DomainException("Local administrator user name is invalid.");
        ArgumentNullException.ThrowIfNull(passwordVerifier);
        if (credentialVersion == Guid.Empty)
            throw new DomainException("Local administrator credential version is invalid.");
        UserName = userName.Trim();
        NormalizedUserName = UserName.ToUpperInvariant();
        PasswordVerifier = passwordVerifier;
        CredentialVersion = credentialVersion;
        UpdatedAt = updatedAt;
    }

    public string UserName { get; }
    public string NormalizedUserName { get; }
    public DeviceSecretVerifier PasswordVerifier { get; }
    public Guid CredentialVersion { get; }
    public DateTimeOffset UpdatedAt { get; }
}

public sealed record LocalAdministratorSession
{
    public LocalAdministratorSession(
        string tokenHash,
        string userName,
        Guid credentialVersion,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        if (string.IsNullOrWhiteSpace(tokenHash) || tokenHash.Length != 64)
            throw new DomainException("Local administrator session token hash is invalid.");
        if (string.IsNullOrWhiteSpace(userName))
            throw new DomainException("Local administrator session user name is required.");
        if (credentialVersion == Guid.Empty)
            throw new DomainException("Local administrator session credential version is invalid.");
        if (expiresAt <= createdAt)
            throw new DomainException("Local administrator session expiry is invalid.");
        TokenHash = tokenHash;
        UserName = userName;
        CredentialVersion = credentialVersion;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public string TokenHash { get; }
    public string UserName { get; }
    public Guid CredentialVersion { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
}
