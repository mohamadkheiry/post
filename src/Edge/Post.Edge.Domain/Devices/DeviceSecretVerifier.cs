using System.Security.Cryptography;
using System.Text;
using Post.Edge.Domain.Common;

namespace Post.Edge.Domain.Devices;

public sealed record DeviceSecretVerifier
{
    public const int MinimumIterations = 100_000;
    public const int MaximumIterations = 2_000_000;
    public const int SaltSizeBytes = 16;
    public const int HashSizeBytes = 32;

    private readonly byte[] _salt;
    private readonly byte[] _hash;

    public DeviceSecretVerifier(
        string algorithm,
        int iterations,
        string saltBase64,
        string hashBase64)
    {
        Algorithm = NormalizeAlgorithm(algorithm);
        if (iterations is < MinimumIterations or > MaximumIterations)
            throw new DomainException("PBKDF2 iterations are outside the supported safe range.");
        Iterations = iterations;
        _salt = Decode(saltBase64, "salt");
        _hash = Decode(hashBase64, "hash");
        if (_salt.Length != SaltSizeBytes) throw new DomainException("PBKDF2 salt must contain exactly 16 bytes.");
        if (_hash.Length != HashSizeBytes) throw new DomainException("PBKDF2 hash must contain exactly 32 bytes.");
        SaltBase64 = Convert.ToBase64String(_salt);
        HashBase64 = Convert.ToBase64String(_hash);
    }

    public string Algorithm { get; }
    public int Iterations { get; }
    public string SaltBase64 { get; }
    public string HashBase64 { get; }

    public bool Verify(string suppliedSecret)
    {
        var hasSuppliedSecret = !string.IsNullOrEmpty(suppliedSecret);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(suppliedSecret ?? string.Empty),
            _salt,
            Iterations,
            Algorithm == "PBKDF2-SHA512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256,
            _hash.Length);
        return hasSuppliedSecret & CryptographicOperations.FixedTimeEquals(_hash, derived);
    }

    public static DeviceSecretVerifier DeriveForDevelopment(
        string secret,
        int iterations = 100_000,
        string algorithm = "PBKDF2-SHA256")
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new DomainException("Development secret is required.");
        if (iterations is < MinimumIterations or > MaximumIterations)
            throw new DomainException("PBKDF2 iterations are outside the supported safe range.");
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var normalized = NormalizeAlgorithm(algorithm);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret),
            salt,
            iterations,
            normalized == "PBKDF2-SHA512" ? HashAlgorithmName.SHA512 : HashAlgorithmName.SHA256,
            HashSizeBytes);
        return new DeviceSecretVerifier(
            normalized,
            iterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    private static string NormalizeAlgorithm(string algorithm) => algorithm?.Trim().ToUpperInvariant() switch
    {
        "PBKDF2-SHA256" or "SHA256" => "PBKDF2-SHA256",
        "PBKDF2-SHA512" or "SHA512" => "PBKDF2-SHA512",
        _ => throw new DomainException("Only PBKDF2-SHA256 and PBKDF2-SHA512 are supported.")
    };

    private static byte[] Decode(string value, string field)
    {
        try { return Convert.FromBase64String(value); }
        catch (Exception ex) when (ex is FormatException or ArgumentNullException)
        {
            throw new DomainException($"PBKDF2 {field} is not valid Base64.");
        }
    }
}
