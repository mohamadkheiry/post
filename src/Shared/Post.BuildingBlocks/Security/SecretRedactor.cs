using System.Collections.ObjectModel;

namespace Post.BuildingBlocks.Security;

/// <summary>Redacts secrets before they are written to logs, audit properties, or diagnostics.</summary>
public interface ISecretRedactor
{
    /// <summary>Whether a property name represents sensitive data.</summary>
    bool IsSensitive(string propertyName);

    /// <summary>Returns a marker instead of secret material.</summary>
    string RedactSecret(string? secret);

    /// <summary>Redacts a value when its property name is sensitive.</summary>
    object? RedactValue(string propertyName, object? value);

    /// <summary>Returns a new read-only dictionary with sensitive values redacted.</summary>
    IReadOnlyDictionary<string, object?> RedactProperties(IEnumerable<KeyValuePair<string, object?>> properties);
}

/// <summary>Case-insensitive secret redactor with normalized property-name matching.</summary>
public sealed class SecretRedactor : ISecretRedactor
{
    /// <summary>Default replacement marker.</summary>
    public const string DefaultMarker = "[REDACTED]";

    private static readonly string[] DefaultSensitiveNames =
    [
        "password", "passphrase", "secret", "token", "deviceToken", "refreshToken",
        "authorization", "apiKey", "cookie", "setCookie", "connectionString",
        "clientSecret", "privateKey", "uploadUri", "presignedUrl"
    ];

    private readonly HashSet<string> _sensitiveNames;
    private readonly string _marker;

    /// <summary>Initializes a redactor, optionally adding application-specific names.</summary>
    public SecretRedactor(IEnumerable<string>? additionalSensitiveNames = null, string marker = DefaultMarker)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        _marker = marker;
        _sensitiveNames = new HashSet<string>(DefaultSensitiveNames.Select(NormalizeName), StringComparer.Ordinal);

        if (additionalSensitiveNames is null)
        {
            return;
        }

        foreach (var name in additionalSensitiveNames)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            _sensitiveNames.Add(NormalizeName(name));
        }
    }

    /// <inheritdoc />
    public bool IsSensitive(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        return _sensitiveNames.Contains(NormalizeName(propertyName));
    }

    /// <inheritdoc />
    public string RedactSecret(string? secret) => secret is null ? string.Empty : _marker;

    /// <inheritdoc />
    public object? RedactValue(string propertyName, object? value) =>
        IsSensitive(propertyName) && value is not null ? _marker : value;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object?> RedactProperties(IEnumerable<KeyValuePair<string, object?>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var redacted = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties)
        {
            redacted[property.Key] = RedactValue(property.Key, property.Value);
        }

        return new ReadOnlyDictionary<string, object?>(redacted);
    }

    private static string NormalizeName(string value)
    {
        var buffer = new char[value.Length];
        var index = 0;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[index++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer, 0, index);
    }
}
