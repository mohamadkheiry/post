using System.Diagnostics.CodeAnalysis;

namespace Post.BuildingBlocks.Results;

/// <summary>Classifies a safe application error.</summary>
public enum ErrorType
{
    /// <summary>No error.</summary>
    None,
    /// <summary>Input or state validation failed.</summary>
    Validation,
    /// <summary>A requested resource was not found.</summary>
    NotFound,
    /// <summary>The caller is unauthenticated.</summary>
    Unauthorized,
    /// <summary>The caller lacks permission.</summary>
    Forbidden,
    /// <summary>A uniqueness or state conflict occurred.</summary>
    Conflict,
    /// <summary>A transient dependency failure occurred.</summary>
    Transient,
    /// <summary>An unexpected failure occurred.</summary>
    Failure
}

/// <summary>A stable machine-readable error with a safe description.</summary>
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Error is the conventional cross-language Result error term and part of the requested public API.")]
public sealed record Error
{
    /// <summary>Canonical absence of an error.</summary>
    public static Error None { get; } = new(string.Empty, string.Empty, ErrorType.None);

    /// <summary>Initializes an error and validates its invariants.</summary>
    /// <param name="code">Stable dotted code.</param>
    /// <param name="description">Safe description.</param>
    /// <param name="type">Classification.</param>
    public Error(string code, string description, ErrorType type)
    {
        if (type is ErrorType.None)
        {
            if (code.Length is not 0 || description.Length is not 0)
            {
                throw new ArgumentException("The None error cannot have a code or description.", nameof(type));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(code);
            ArgumentException.ThrowIfNullOrWhiteSpace(description);
        }

        Code = code;
        Description = description;
        Type = type;
    }

    /// <summary>Stable machine-readable code.</summary>
    public string Code { get; }

    /// <summary>Safe human-readable description.</summary>
    public string Description { get; }

    /// <summary>Error classification.</summary>
    public ErrorType Type { get; }

    /// <summary>Creates a validation error.</summary>
    public static Error Validation(string code, string description) => new(code, description, ErrorType.Validation);

    /// <summary>Creates a not-found error.</summary>
    public static Error NotFound(string code, string description) => new(code, description, ErrorType.NotFound);

    /// <summary>Creates an unauthorized error.</summary>
    public static Error Unauthorized(string code, string description) => new(code, description, ErrorType.Unauthorized);

    /// <summary>Creates a forbidden error.</summary>
    public static Error Forbidden(string code, string description) => new(code, description, ErrorType.Forbidden);

    /// <summary>Creates a conflict error.</summary>
    public static Error Conflict(string code, string description) => new(code, description, ErrorType.Conflict);

    /// <summary>Creates a transient error.</summary>
    public static Error Transient(string code, string description) => new(code, description, ErrorType.Transient);

    /// <summary>Creates a general failure.</summary>
    public static Error Failure(string code, string description) => new(code, description, ErrorType.Failure);

    /// <inheritdoc />
    public override string ToString() => Type is ErrorType.None ? "None" : $"{Code}: {Description}";
}
