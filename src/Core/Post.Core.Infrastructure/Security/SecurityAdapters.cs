using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Devices;

namespace Post.Core.Infrastructure.Security;

public sealed class Pbkdf2SecretHasher : ISecretHasher
{
    public const string AlgorithmName = SecretVerifier.AlgorithmName;

    private const int SaltLength = SecretVerifier.SaltLength;
    private const int HashLength = SecretVerifier.HashLength;
    private static readonly SecretVerifier Dummy = new(
        Convert.ToBase64String(new byte[HashLength]),
        Convert.ToBase64String(new byte[SaltLength]),
        210_000,
        AlgorithmName);

    public SecretVerifier DummyVerifier => Dummy;

    public string GenerateSecret(int byteLength = 32)
    {
        if (byteLength < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "Secrets must contain at least 128 bits of entropy.");
        }

        return Convert.ToHexString(RandomNumberGenerator.GetBytes(byteLength));
    }

    public SecretVerifier Hash(string secret, int iterations = 210_000)
    {
        ValidateSecret(secret);
        if (iterations is < SecretVerifier.MinimumIterations or > SecretVerifier.MaximumIterations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                $"PBKDF2 iterations must be between {SecretVerifier.MinimumIterations:N0} and {SecretVerifier.MaximumIterations:N0}.");
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashLength);
        return new SecretVerifier(
            Convert.ToBase64String(hash),
            Convert.ToBase64String(salt),
            iterations,
            AlgorithmName);
    }

    public bool Verify(string secret, SecretVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        if (string.IsNullOrEmpty(secret) ||
            secret.Length > AuthenticationInputLimits.DeviceSecretMaximumLength ||
            !string.Equals(verifier.Algorithm, AlgorithmName, StringComparison.Ordinal) ||
            verifier.Iterations is < SecretVerifier.MinimumIterations or > SecretVerifier.MaximumIterations)
        {
            return false;
        }

        Span<byte> salt = stackalloc byte[SaltLength];
        Span<byte> expected = stackalloc byte[HashLength];
        if (verifier.Salt is null ||
            verifier.Salt.Length != 24 ||
            !Convert.TryFromBase64String(verifier.Salt, salt, out var saltBytesWritten) ||
            saltBytesWritten != SaltLength ||
            !string.Equals(Convert.ToBase64String(salt), verifier.Salt, StringComparison.Ordinal) ||
            verifier.Hash is null ||
            verifier.Hash.Length != 44 ||
            !Convert.TryFromBase64String(verifier.Hash, expected, out var hashBytesWritten) ||
            hashBytesWritten != HashLength ||
            !string.Equals(Convert.ToBase64String(expected), verifier.Hash, StringComparison.Ordinal))
        {
            return false;
        }

        Span<byte> actual = stackalloc byte[HashLength];
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            actual,
            verifier.Iterations,
            HashAlgorithmName.SHA256);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static void ValidateSecret(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) ||
            secret.Length > AuthenticationInputLimits.DeviceSecretMaximumLength)
        {
            throw new ArgumentException(
                $"A non-empty secret of at most {AuthenticationInputLimits.DeviceSecretMaximumLength} characters is required.",
                nameof(secret));
        }
    }
}

public sealed record HmacJwtOptions
{
    public required string Issuer { get; init; }

    public required string Audience { get; init; }

    public required string SigningKey { get; init; }
}

public sealed class HmacJwtTokenIssuer : ITokenIssuer
{
    private static readonly HashSet<string> ReservedClaims = new(StringComparer.Ordinal)
    {
        JwtRegisteredClaimNames.Iss,
        JwtRegisteredClaimNames.Aud,
        JwtRegisteredClaimNames.Sub,
        JwtRegisteredClaimNames.Iat,
        JwtRegisteredClaimNames.Nbf,
        JwtRegisteredClaimNames.Exp,
        JwtRegisteredClaimNames.Jti,
        "role",
        "allowed_center"
    };

    private readonly HmacJwtOptions _options;
    private readonly SigningCredentials _signingCredentials;
    private readonly JwtSecurityTokenHandler _handler = new();

    public HmacJwtTokenIssuer(HmacJwtOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new ArgumentException("JWT issuer and audience are required.", nameof(options));
        }

        var key = Encoding.UTF8.GetBytes(options.SigningKey ?? string.Empty);
        if (key.Length < 32)
        {
            throw new ArgumentException("JWT signing key must be at least 256 bits.", nameof(options));
        }

        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(key),
            SecurityAlgorithms.HmacSha256);
    }

    public AccessTokenResult Issue(TokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ExpiresAt <= descriptor.IssuedAt)
        {
            throw new CoreValidationException("Token expiration must be after its issue time.");
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, descriptor.Subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(
                JwtRegisteredClaimNames.Iat,
                descriptor.IssuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64)
        };
        claims.AddRange(descriptor.Roles.Select(role => new Claim("role", role)));
        claims.AddRange(descriptor.AllowedCenters.Select(center => new Claim("allowed_center", center)));
        foreach (var claim in descriptor.AdditionalClaims)
        {
            if (ReservedClaims.Contains(claim.Key))
            {
                throw new CoreValidationException($"The additional JWT claim '{claim.Key}' is reserved.");
            }

            claims.Add(new Claim(claim.Key, claim.Value));
        }

        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            descriptor.IssuedAt.UtcDateTime,
            descriptor.ExpiresAt.UtcDateTime,
            _signingCredentials);
        return new AccessTokenResult(_handler.WriteToken(token), descriptor.ExpiresAt);
    }
}
