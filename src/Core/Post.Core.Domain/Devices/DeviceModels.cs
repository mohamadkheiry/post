using System.ComponentModel.DataAnnotations.Schema;
using Post.Core.Domain.Common;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Domain.Devices;

public sealed record SecretVerifier
{
    public const string AlgorithmName = "PBKDF2-SHA256";
    public const int SaltLength = 16;
    public const int HashLength = 32;
    public const int MinimumIterations = 100_000;
    public const int MaximumIterations = 1_000_000;

    public SecretVerifier(string hash, string salt, int iterations, string algorithm = AlgorithmName)
    {
        if (!string.Equals(algorithm, AlgorithmName, StringComparison.Ordinal))
        {
            throw new DomainRuleException($"Secret algorithm must be {AlgorithmName}.");
        }

        if (iterations is < MinimumIterations or > MaximumIterations)
        {
            throw new DomainRuleException(
                $"PBKDF2 iterations must be between {MinimumIterations:N0} and {MaximumIterations:N0}.");
        }

        Hash = ValidateCanonicalBase64(hash, HashLength, "Secret hash");
        Salt = ValidateCanonicalBase64(salt, SaltLength, "Secret salt");
        Iterations = iterations;
        Algorithm = algorithm;
    }

    public string Hash { get; }

    public string Salt { get; }

    public int Iterations { get; }

    public string Algorithm { get; }

    private static string ValidateCanonicalBase64(string value, int decodedLength, string fieldName)
    {
        var encodedLength = ((decodedLength + 2) / 3) * 4;
        if (string.IsNullOrWhiteSpace(value) || value.Length != encodedLength)
        {
            throw new DomainRuleException($"{fieldName} must be canonical Base64 for exactly {decodedLength} bytes.");
        }

        Span<byte> decoded = stackalloc byte[decodedLength];
        if (!Convert.TryFromBase64String(value, decoded, out var bytesWritten) ||
            bytesWritten != decodedLength ||
            !string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal))
        {
            throw new DomainRuleException($"{fieldName} must be canonical Base64 for exactly {decodedLength} bytes.");
        }

        return value;
    }
}

public sealed class SortingDevice : IAggregateRoot
{
    public static readonly TimeSpan DefaultSecretRotationGrace = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MinimumSecretRotationGrace = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumSecretRotationGrace = TimeSpan.FromHours(24);

    private SortingDevice()
    {
        Name = string.Empty;
        DeviceType = string.Empty;
        Owner = string.Empty;
        Description = string.Empty;
        SecretVerifier = new SecretVerifier(
            Convert.ToBase64String(new byte[SecretVerifier.HashLength]),
            Convert.ToBase64String(new byte[SecretVerifier.SaltLength]),
            SecretVerifier.MinimumIterations);
    }

    private SortingDevice(
        DeviceId id,
        string name,
        string deviceType,
        LogicalDeviceCode logicalCode,
        string owner,
        ExchangeCenterCode exchangeCenterCode,
        string description,
        SecretVerifier secretVerifier)
    {
        Id = id;
        Name = Required(name, nameof(name), 128);
        DeviceType = Required(deviceType, nameof(deviceType), 64);
        LogicalCode = logicalCode;
        Owner = Required(owner, nameof(owner), 128);
        ExchangeCenterCode = exchangeCenterCode;
        Description = Optional(description, 512);
        SecretVerifier = secretVerifier ?? throw new ArgumentNullException(nameof(secretVerifier));
        ActivationStatus = DeviceActivationStatus.Active;
    }

    public DeviceId Id { get; private set; }

    public string Name { get; private set; }

    public string DeviceType { get; private set; }

    public LogicalDeviceCode LogicalCode { get; private set; }

    public string Owner { get; private set; }

    public ExchangeCenterCode ExchangeCenterCode { get; private set; }

    public string Description { get; private set; }

    public SecretVerifier SecretVerifier { get; private set; }

    public string? PreviousSecretHash { get; private set; }

    public string? PreviousSecretSalt { get; private set; }

    public int? PreviousSecretIterations { get; private set; }

    public string? PreviousSecretAlgorithm { get; private set; }

    public DateTimeOffset? PreviousSecretValidUntil { get; private set; }

    [NotMapped]
    public SecretVerifier? PreviousSecretVerifier =>
        PreviousSecretHash is not null &&
        PreviousSecretSalt is not null &&
        PreviousSecretIterations is { } iterations &&
        PreviousSecretAlgorithm is not null
            ? new SecretVerifier(
                PreviousSecretHash,
                PreviousSecretSalt,
                iterations,
                PreviousSecretAlgorithm)
            : null;

    public DeviceActivationStatus ActivationStatus { get; private set; }

    public static SortingDevice Create(
        DeviceId id,
        string name,
        string deviceType,
        LogicalDeviceCode logicalCode,
        string owner,
        ExchangeCenterCode exchangeCenterCode,
        string description,
        SecretVerifier secretVerifier) =>
        new(id, name, deviceType, logicalCode, owner, exchangeCenterCode, description, secretVerifier);

    public void UpdateDescriptiveInformation(
        string name,
        string deviceType,
        string owner,
        string description)
    {
        Name = Required(name, nameof(name), 128);
        DeviceType = Required(deviceType, nameof(deviceType), 64);
        Owner = Required(owner, nameof(owner), 128);
        Description = Optional(description, 512);
    }

    public void ReplaceSecretVerifier(SecretVerifier secretVerifier)
    {
        SecretVerifier = secretVerifier ?? throw new ArgumentNullException(nameof(secretVerifier));
        ClearPreviousSecretVerifier();
    }

    public void RotateSecretVerifier(
        SecretVerifier secretVerifier,
        DateTimeOffset rotatedAt,
        TimeSpan gracePeriod)
    {
        ArgumentNullException.ThrowIfNull(secretVerifier);
        if (gracePeriod < MinimumSecretRotationGrace || gracePeriod > MaximumSecretRotationGrace)
        {
            throw new DomainRuleException(
                $"Device-secret rotation grace must be between {MinimumSecretRotationGrace} and " +
                $"{MaximumSecretRotationGrace}.");
        }

        PreviousSecretHash = SecretVerifier.Hash;
        PreviousSecretSalt = SecretVerifier.Salt;
        PreviousSecretIterations = SecretVerifier.Iterations;
        PreviousSecretAlgorithm = SecretVerifier.Algorithm;
        PreviousSecretValidUntil = rotatedAt.Add(gracePeriod);
        SecretVerifier = secretVerifier;
    }

    public void Activate() => ActivationStatus = DeviceActivationStatus.Active;

    public void Deactivate() => ActivationStatus = DeviceActivationStatus.Inactive;

    private void ClearPreviousSecretVerifier()
    {
        PreviousSecretHash = null;
        PreviousSecretSalt = null;
        PreviousSecretIterations = null;
        PreviousSecretAlgorithm = null;
        PreviousSecretValidUntil = null;
    }

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength)
        {
            throw new DomainRuleException($"{name} is required and cannot exceed {maxLength} characters.");
        }

        return value.Trim();
    }

    private static string Optional(string? value, int maxLength)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length > maxLength)
        {
            throw new DomainRuleException($"Value cannot exceed {maxLength} characters.");
        }

        return result;
    }
}

public sealed class ConfigurationSnapshot : IAggregateRoot
{
    private readonly List<ConfigurationDevice> _devices = [];
    private readonly List<ConfigurationPostalCode> _postalCodes = [];

    private ConfigurationSnapshot()
    {
        Settings = OperationalSettings.Default;
    }

    private ConfigurationSnapshot(
        ExchangeCenterCode exchangeCenterCode,
        ConfigurationVersion version,
        DateTimeOffset generatedAt,
        IEnumerable<SortingDevice> devices,
        IEnumerable<ExchangeCenterCode> postalCodes,
        OperationalSettings settings)
    {
        var deviceArray = devices.ToArray();
        if (deviceArray.Any(x => x.ExchangeCenterCode != exchangeCenterCode))
        {
            throw new DomainRuleException("Every device in a snapshot must belong to the snapshot center.");
        }

        ExchangeCenterCode = exchangeCenterCode;
        Version = version;
        GeneratedAt = generatedAt;
        ArgumentNullException.ThrowIfNull(settings);
        Settings = new OperationalSettings(
            settings.ShortReadingThreshold,
            settings.ReturnThreshold,
            new DiscrepancyTolerance(
                settings.DiscrepancyTolerance.WeightKg,
                settings.DiscrepancyTolerance.DimensionCm),
            settings.AutoDispatchEnabled,
            settings.MaximumOpenBagDuration);
        _devices.AddRange(deviceArray.Select(x =>
            ConfigurationDevice.FromSnapshot(exchangeCenterCode, version, generatedAt, x)));
        _postalCodes.AddRange(postalCodes
            .Distinct()
            .Select(x => ConfigurationPostalCode.Create(exchangeCenterCode, version, x)));
    }

    public ExchangeCenterCode ExchangeCenterCode { get; private set; }

    public ConfigurationVersion Version { get; private set; }

    public DateTimeOffset GeneratedAt { get; private set; }

    public OperationalSettings Settings { get; private set; }

    public IReadOnlyCollection<ConfigurationDevice> Devices => _devices.AsReadOnly();

    public IReadOnlyCollection<ConfigurationPostalCode> PostalCodes => _postalCodes.AsReadOnly();

    public static ConfigurationSnapshot Create(
        ExchangeCenterCode exchangeCenterCode,
        ConfigurationVersion version,
        DateTimeOffset generatedAt,
        IEnumerable<SortingDevice> devices,
        IEnumerable<ExchangeCenterCode> postalCodes,
        OperationalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(postalCodes);
        return new ConfigurationSnapshot(
            exchangeCenterCode,
            version,
            generatedAt,
            devices,
            postalCodes,
            settings);
    }
}

public sealed class ConfigurationDevice
{
    private ConfigurationDevice()
    {
        Name = string.Empty;
        DeviceType = string.Empty;
        SecretHash = string.Empty;
        SecretSalt = string.Empty;
        SecretAlgorithm = string.Empty;
    }

    private ConfigurationDevice(
        ExchangeCenterCode center,
        ConfigurationVersion version,
        DeviceId deviceId,
        LogicalDeviceCode logicalCode,
        string name,
        string deviceType,
        DeviceActivationStatus status,
        SecretVerifier verifier,
        SecretVerifier? previousVerifier,
        DateTimeOffset? previousValidUntil)
    {
        ExchangeCenterCode = center;
        Version = version;
        DeviceId = deviceId;
        LogicalCode = logicalCode;
        Name = name;
        DeviceType = deviceType;
        ActivationStatus = status;
        SecretHash = verifier.Hash;
        SecretSalt = verifier.Salt;
        SecretIterations = verifier.Iterations;
        SecretAlgorithm = verifier.Algorithm;
        PreviousSecretHash = previousVerifier?.Hash;
        PreviousSecretSalt = previousVerifier?.Salt;
        PreviousSecretIterations = previousVerifier?.Iterations;
        PreviousSecretAlgorithm = previousVerifier?.Algorithm;
        PreviousSecretValidUntil = previousValidUntil;
    }

    public ExchangeCenterCode ExchangeCenterCode { get; private set; }

    public ConfigurationVersion Version { get; private set; }

    public DeviceId DeviceId { get; private set; }

    public LogicalDeviceCode LogicalCode { get; private set; }

    public string Name { get; private set; }

    public string DeviceType { get; private set; }

    public DeviceActivationStatus ActivationStatus { get; private set; }

    public string SecretHash { get; private set; }

    public string SecretSalt { get; private set; }

    public int SecretIterations { get; private set; }

    public string SecretAlgorithm { get; private set; }

    public string? PreviousSecretHash { get; private set; }

    public string? PreviousSecretSalt { get; private set; }

    public int? PreviousSecretIterations { get; private set; }

    public string? PreviousSecretAlgorithm { get; private set; }

    public DateTimeOffset? PreviousSecretValidUntil { get; private set; }

    // Intentionally no raw device secret. The PBKDF2 verifier is safe to distribute to the
    // owning Edge for offline verification and cannot be used as the original credential.
    internal static ConfigurationDevice FromSnapshot(
        ExchangeCenterCode center,
        ConfigurationVersion version,
        DateTimeOffset generatedAt,
        SortingDevice device)
    {
        var previousValidUntil = device.PreviousSecretValidUntil;
        var previousVerifier = previousValidUntil > generatedAt
            ? device.PreviousSecretVerifier
            : null;
        return new ConfigurationDevice(
            center,
            version,
            device.Id,
            device.LogicalCode,
            device.Name,
            device.DeviceType,
            device.ActivationStatus,
            device.SecretVerifier,
            previousVerifier,
            previousVerifier is null ? null : previousValidUntil);
    }
}

public sealed class ConfigurationPostalCode
{
    private ConfigurationPostalCode()
    {
    }

    private ConfigurationPostalCode(
        ExchangeCenterCode center,
        ConfigurationVersion version,
        ExchangeCenterCode postalCode)
    {
        ExchangeCenterCode = center;
        Version = version;
        PostalCode = postalCode;
    }

    public ExchangeCenterCode ExchangeCenterCode { get; private set; }

    public ConfigurationVersion Version { get; private set; }

    public ExchangeCenterCode PostalCode { get; private set; }

    internal static ConfigurationPostalCode Create(
        ExchangeCenterCode center,
        ConfigurationVersion version,
        ExchangeCenterCode postalCode) =>
        new(center, version, postalCode);
}
