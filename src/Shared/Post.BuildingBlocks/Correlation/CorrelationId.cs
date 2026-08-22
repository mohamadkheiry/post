namespace Post.BuildingBlocks.Correlation;

/// <summary>Correlation-id validation and generation rules shared by HTTP and messaging boundaries.</summary>
public static class CorrelationId
{
    /// <summary>Canonical HTTP header.</summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>Maximum accepted identifier length.</summary>
    public const int MaximumLength = 128;

    /// <summary>Returns whether an untrusted identifier is safe for logs and headers.</summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaximumLength)
        {
            return false;
        }

        foreach (var character in trimmed)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns a trimmed safe id or creates a new lowercase GUID id.</summary>
    public static string NormalizeOrCreate(string? value) =>
        IsValid(value) ? value!.Trim() : Guid.NewGuid().ToString("N");
}
