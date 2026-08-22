using System.Security.Cryptography;
using System.Text;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Devices;

namespace Post.Edge.Application.Services;

public sealed class LocalAdministratorService(
    ILocalAdministratorRepository repository,
    IEdgeUnitOfWork unitOfWork,
    IClock clock) : ILocalAdministratorService
{
    private const int PasswordIterations = 310_000;
    private static readonly byte[] DummySalt = Convert.FromHexString("E717B88370D0770B3342F20E10C1EF06");
    private static readonly byte[] DummyHash = Convert.FromHexString(
        "AD9207889E756DEFF962506B8A67432710A4E473147559700176E581141F9F23");

    public async Task EnsureInitializedAsync(
        string? initialUserName,
        string? initialPassword,
        CancellationToken cancellationToken)
    {
        if (await repository.ExistsAsync(cancellationToken))
        {
            if (await repository.GetAsync(cancellationToken) is null)
                throw new InvalidOperationException("The persisted local administrator verifier is invalid.");
            await repository.RemoveExpiredSessionsAsync(clock.UtcNow, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return;
        }
        ValidateUserName(initialUserName);
        ValidatePassword(initialPassword);
        await repository.StoreAsync(new LocalAdministrator(
            initialUserName!,
            CreateVerifier(initialPassword!),
            Guid.NewGuid(),
            clock.UtcNow), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<LocalAdministratorLoginResult?> LoginAsync(
        LocalAdministratorLoginCommand command,
        TimeSpan sessionLifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var userNameIsBounded = !string.IsNullOrWhiteSpace(command.UserName) &&
                                command.UserName.Length <= LocalAdministratorInputLimits.MaximumUserNameCharacters;
        var passwordIsBounded = !string.IsNullOrEmpty(command.Password) &&
                                command.Password.Length <= LocalAdministratorInputLimits.MaximumPasswordCharacters;
        var boundedPassword = passwordIsBounded ? command.Password : "invalid-local-administrator-password";
        var administrator = await repository.GetAsync(cancellationToken);
        var userMatches = userNameIsBounded && administrator is not null &&
                          string.Equals(
                              administrator.NormalizedUserName,
                              command.UserName.Trim().ToUpperInvariant(),
                              StringComparison.Ordinal);
        var passwordMatches = Verify(administrator?.PasswordVerifier, boundedPassword);
        if (!userMatches || !passwordIsBounded || !passwordMatches) return null;
        if (sessionLifetime < TimeSpan.FromMinutes(1) || sessionLifetime > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(sessionLifetime));

        await repository.RemoveExpiredSessionsAsync(clock.UtcNow, cancellationToken);
        var rawToken = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = clock.UtcNow + sessionLifetime;
        repository.AddSession(new LocalAdministratorSession(
            HashToken(rawToken),
            administrator!.UserName,
            administrator.CredentialVersion,
            clock.UtcNow,
            expiresAt));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new LocalAdministratorLoginResult(rawToken, expiresAt);
    }

    public async Task<string?> AuthenticateSessionAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) ||
            accessToken.Length > LocalAdministratorInputLimits.MaximumTokenCharacters)
            return null;
        var session = await repository.FindSessionAsync(HashToken(accessToken), cancellationToken);
        if (session is null) return null;
        var administrator = await repository.GetAsync(cancellationToken);
        if (session.ExpiresAt > clock.UtcNow &&
            administrator is not null &&
            administrator.CredentialVersion == session.CredentialVersion &&
            string.Equals(administrator.UserName, session.UserName, StringComparison.Ordinal))
        {
            return session.UserName;
        }
        await repository.RemoveSessionAsync(session.TokenHash, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return null;
    }

    public async Task LogoutAsync(string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken) ||
            accessToken.Length > LocalAdministratorInputLimits.MaximumTokenCharacters)
            return;
        await repository.RemoveSessionAsync(HashToken(accessToken), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RotatePasswordAsync(
        RotateLocalAdministratorPasswordCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var suppliedUserName = command.UserName ?? string.Empty;
        var currentPassword = command.CurrentPassword ?? string.Empty;
        var newPassword = command.NewPassword ?? string.Empty;
        var administrator = await repository.GetAsync(cancellationToken);
        var userMatches = administrator is not null &&
                          suppliedUserName.Length <= LocalAdministratorInputLimits.MaximumUserNameCharacters &&
                          string.Equals(administrator.NormalizedUserName,
                              suppliedUserName.Trim().ToUpperInvariant(), StringComparison.Ordinal);
        var currentIsBounded = currentPassword.Length <=
                               LocalAdministratorInputLimits.MaximumPasswordCharacters;
        var currentMatches = Verify(
            administrator?.PasswordVerifier,
            currentIsBounded ? currentPassword : "invalid-local-administrator-password");
        if (!userMatches || !currentIsBounded || !currentMatches) return false;
        ValidatePassword(newPassword);
        await repository.StoreAsync(new LocalAdministrator(
            administrator!.UserName,
            CreateVerifier(newPassword),
            Guid.NewGuid(),
            clock.UtcNow), cancellationToken);
        await repository.RemoveAllSessionsAsync(cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal static string HashToken(string accessToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));

    private static DeviceSecretVerifier CreateVerifier(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordIterations,
            HashAlgorithmName.SHA256,
            32);
        return new DeviceSecretVerifier(
            "PBKDF2-SHA256",
            PasswordIterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    private static bool Verify(DeviceSecretVerifier? verifier, string password)
    {
        var configured = verifier is not null;
        var iterations = verifier?.Iterations ?? PasswordIterations;
        var salt = configured ? Convert.FromBase64String(verifier!.SaltBase64) : DummySalt;
        var expected = configured ? Convert.FromBase64String(verifier!.HashBase64) : DummyHash;
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && configured;
    }

    private static void ValidateUserName(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName) ||
            userName.Length > LocalAdministratorInputLimits.MaximumUserNameCharacters)
            throw new InvalidOperationException("A bounded local administrator user name is required for first run.");
    }

    private static void ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) ||
            password.Length < LocalAdministratorInputLimits.MinimumPasswordCharacters ||
            password.Length > LocalAdministratorInputLimits.MaximumPasswordCharacters)
            throw new InvalidOperationException("A strong bounded local administrator password is required.");
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
