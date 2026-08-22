using Post.BuildingBlocks.Results;
using System.Globalization;

namespace PostShared.Tests.BuildingBlocks;

public sealed class ResultTests
{
    [Fact]
    public void ErrorNoneIsEmptyAndFormatsAsNone()
    {
        Assert.Equal(ErrorType.None, Error.None.Type);
        Assert.Equal(string.Empty, Error.None.Code);
        Assert.Equal("None", Error.None.ToString());
    }

    [Fact]
    public void ErrorConstructorEnforcesNoneAndConcreteErrorInvariants()
    {
        var error = new Error("parcel.invalid", "Parcel is invalid.", ErrorType.Validation);

        Assert.Equal("parcel.invalid", error.Code);
        Assert.Equal("Parcel is invalid.", error.Description);
        Assert.Equal(ErrorType.Validation, error.Type);
        Assert.Throws<ArgumentException>(() => new Error("code", "description", ErrorType.None));
        Assert.Throws<ArgumentException>(() => new Error(" ", "description", ErrorType.Failure));
        Assert.Throws<ArgumentException>(() => new Error("code", " ", ErrorType.Failure));
    }

    [Theory]
    [InlineData(ErrorType.Validation)]
    [InlineData(ErrorType.NotFound)]
    [InlineData(ErrorType.Unauthorized)]
    [InlineData(ErrorType.Forbidden)]
    [InlineData(ErrorType.Conflict)]
    [InlineData(ErrorType.Transient)]
    [InlineData(ErrorType.Failure)]
    public void ErrorFactoriesCreateRequestedClassification(ErrorType type)
    {
        var error = type switch
        {
            ErrorType.Validation => Error.Validation("code", "description"),
            ErrorType.NotFound => Error.NotFound("code", "description"),
            ErrorType.Unauthorized => Error.Unauthorized("code", "description"),
            ErrorType.Forbidden => Error.Forbidden("code", "description"),
            ErrorType.Conflict => Error.Conflict("code", "description"),
            ErrorType.Transient => Error.Transient("code", "description"),
            ErrorType.Failure => Error.Failure("code", "description"),
            _ => throw new InvalidOperationException()
        };

        Assert.Equal(type, error.Type);
        Assert.Equal("code: description", error.ToString());
    }

    [Fact]
    public void ResultSuccessAndFailureExposeExpectedState()
    {
        var error = Error.Conflict("duplicate", "Already exists.");
        var success = Result.Success();
        var failure = Result.Failure(error);

        Assert.True(success.IsSuccess);
        Assert.False(success.IsFailure);
        Assert.Equal(Error.None, success.Error);
        Assert.True(failure.IsFailure);
        Assert.Same(error, failure.Error);
        Assert.Throws<ArgumentException>(() => Result.Failure(Error.None));
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void ResultMatchExecutesOnlySelectedBranchAndValidatesDelegates()
    {
        var error = Error.Failure("failed", "failed");
        Assert.Equal("yes", Result.Success().Match(() => "yes", _ => "no"));
        Assert.Equal("failed", Result.Failure(error).Match(() => "yes", current => current.Code));
        Assert.Throws<ArgumentNullException>(() => Result.Success().Match<string>(null!, _ => "no"));
        Assert.Throws<ArgumentNullException>(() => Result.Success().Match(() => "yes", null!));
    }

    [Fact]
    public void GenericResultSuccessExposesAndReturnsValue()
    {
        var result = Result.Success("value");

        Assert.True(result.IsSuccess);
        Assert.Equal("value", result.Value);
        Assert.True(result.TryGetValue(out var value));
        Assert.Equal("value", value);
        Assert.Throws<ArgumentNullException>(() => Result.Success<string>(null!));
    }

    [Fact]
    public void GenericResultFailureHidesValueAndReturnsError()
    {
        var error = Error.NotFound("missing", "missing");
        var result = Result.Failure<string>(error);

        Assert.True(result.IsFailure);
        Assert.False(result.TryGetValue(out var value));
        Assert.Null(value);
        Assert.Same(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Throws<ArgumentException>(() => Result.Failure<string>(Error.None));
    }

    [Fact]
    public void GenericResultMapMapsSuccessAndPreservesFailure()
    {
        var error = Error.Failure("failed", "failed");
        var mappedSuccess = Result.Success(2).Map(value => value * 3);
        var mappedFailure = Result.Failure<int>(error).Map(value => value * 3);

        Assert.Equal(6, mappedSuccess.Value);
        Assert.Same(error, mappedFailure.Error);
        Assert.Throws<ArgumentNullException>(() => Result.Success(1).Map<string>(null!));
    }

    [Fact]
    public void GenericResultBindBindsSuccessAndPreservesFailure()
    {
        var error = Error.Failure("failed", "failed");
        var boundSuccess = Result.Success(2).Bind(value => Result.Success(value.ToString(CultureInfo.InvariantCulture)));
        var boundFailure = Result.Failure<int>(error).Bind(value => Result.Success(value.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal("2", boundSuccess.Value);
        Assert.Same(error, boundFailure.Error);
        Assert.Throws<ArgumentNullException>(() => Result.Success(1).Bind<string>(null!));
    }

    [Fact]
    public void GenericResultMatchExecutesSelectedBranchAndValidatesDelegates()
    {
        var error = Error.Validation("invalid", "invalid");

        Assert.Equal("value", Result.Success("value").Match(value => value, _ => "error"));
        Assert.Equal("invalid", Result.Failure<string>(error).Match(value => value, current => current.Code));
        Assert.Throws<ArgumentNullException>(() => Result.Success("value").Match<string>(null!, _ => "error"));
        Assert.Throws<ArgumentNullException>(() => Result.Success("value").Match(value => value, null!));
    }
}
