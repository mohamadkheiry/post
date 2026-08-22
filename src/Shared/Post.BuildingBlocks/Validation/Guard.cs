namespace Post.BuildingBlocks.Validation;

/// <summary>Small dependency-free guard helpers for public boundaries and constructors.</summary>
public static class Guard
{
    /// <summary>Rejects null and returns the value.</summary>
    public static T NotNull<T>(T? value, string parameterName) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return value;
    }

    /// <summary>Rejects null, empty, and whitespace strings and returns a trimmed value.</summary>
    public static string NotNullOrWhiteSpace(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    /// <summary>Rejects an empty GUID.</summary>
    public static Guid NotEmpty(Guid value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, Guid.Empty, parameterName);
        return value;
    }

    /// <summary>Rejects the default value for a value type.</summary>
    public static T NotDefault<T>(T value, string parameterName) where T : struct
    {
        if (EqualityComparer<T>.Default.Equals(value, default))
        {
            throw new ArgumentException("Value cannot be the default value.", parameterName);
        }

        return value;
    }

    /// <summary>Requires an inclusive comparable range.</summary>
    public static T InRange<T>(T value, T minimum, T maximum, string parameterName) where T : IComparable<T>
    {
        if (minimum.CompareTo(maximum) > 0)
        {
            throw new ArgumentException("Minimum cannot exceed maximum.", nameof(minimum));
        }

        if (value.CompareTo(minimum) < 0 || value.CompareTo(maximum) > 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Value must be between {minimum} and {maximum}.");
        }

        return value;
    }

    /// <summary>Rejects a negative integer.</summary>
    public static int NotNegative(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }

    /// <summary>Rejects zero and negative integers.</summary>
    public static int Positive(int value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, parameterName);
        return value;
    }

    /// <summary>Throws when a caller-supplied invalid condition is true.</summary>
    public static void Against(bool invalidCondition, string message, string parameterName)
    {
        if (invalidCondition)
        {
            throw new ArgumentException(message, parameterName);
        }
    }
}
