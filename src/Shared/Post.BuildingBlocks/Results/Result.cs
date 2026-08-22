using System.Diagnostics.CodeAnalysis;

namespace Post.BuildingBlocks.Results;

/// <summary>Represents success or a safe expected failure.</summary>
public class Result
{
    private protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (isSuccess == (error.Type is not ErrorType.None))
        {
            throw new ArgumentException("Success must have Error.None and failure must have a concrete error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>Whether the operation succeeded.</summary>
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    /// <summary>Whether the operation failed.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Error or <see cref="Results.Error.None"/>.</summary>
    public Error Error { get; }

    /// <summary>Creates a successful result.</summary>
    public static Result Success() => new(true, Error.None);

    /// <summary>Creates a failed result.</summary>
    public static Result Failure(Error error) => new(false, error);

    /// <summary>Creates a successful value result.</summary>
    public static Result<TValue> Success<TValue>(TValue value) => new(value);

    /// <summary>Creates a failed value result.</summary>
    public static Result<TValue> Failure<TValue>(Error error) => new(error);

    /// <summary>Projects success or failure without exceptions.</summary>
    public TResult Match<TResult>(Func<TResult> onSuccess, Func<Error, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess() : onFailure(Error);
    }
}

/// <summary>Represents success with a value or a safe expected failure.</summary>
/// <typeparam name="TValue">Success value.</typeparam>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue value)
        : base(true, Error.None)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    internal Result(Error error)
        : base(false, error)
    {
    }

    /// <summary>Success value; throws when accessed on failure.</summary>
    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("A failed result has no value.");

    /// <summary>Returns the success value without throwing.</summary>
    public bool TryGetValue([MaybeNullWhen(false)] out TValue value)
    {
        value = _value;
        return IsSuccess;
    }

    /// <summary>Maps a successful value, preserving a failure unchanged.</summary>
    public Result<TNext> Map<TNext>(Func<TValue, TNext> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return IsSuccess ? Result.Success(map(Value)) : Result.Failure<TNext>(Error);
    }

    /// <summary>Binds a successful value to another expected operation.</summary>
    public Result<TNext> Bind<TNext>(Func<TValue, Result<TNext>> bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return IsSuccess ? bind(Value) : Result.Failure<TNext>(Error);
    }

    /// <summary>Projects success or failure without exceptions.</summary>
    public TResult Match<TResult>(Func<TValue, TResult> onSuccess, Func<Error, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        ArgumentNullException.ThrowIfNull(onFailure);
        return IsSuccess ? onSuccess(Value) : onFailure(Error);
    }
}
