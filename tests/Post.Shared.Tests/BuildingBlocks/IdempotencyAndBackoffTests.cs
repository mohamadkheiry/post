using Post.BuildingBlocks.Idempotency;
using Post.BuildingBlocks.Messaging;

namespace PostShared.Tests.BuildingBlocks;

public sealed class IdempotencyAndBackoffTests
{
    [Fact]
    public void IdempotencyKeyCreateNormalizesValidPartsAndFormatsStably()
    {
        var key = IdempotencyKey.Create(" bag-close ", " operation-1 ");

        Assert.Equal("bag-close", key.Scope);
        Assert.Equal("operation-1", key.Value);
        Assert.Equal("bag-close:operation-1", key.ToString());
    }

    [Fact]
    public void IdempotencyKeyCreateRejectsBlankLongAndControlCharacters()
    {
        Assert.Throws<ArgumentException>(() => IdempotencyKey.Create(" ", "value"));
        Assert.Throws<ArgumentException>(() => IdempotencyKey.Create("scope", " "));
        Assert.Throws<ArgumentException>(() => IdempotencyKey.Create(new string('s', 201), "value"));
        Assert.Throws<ArgumentException>(() => IdempotencyKey.Create("scope", "bad\nvalue"));
    }

    [Theory]
    [InlineData("scope", "value", true)]
    [InlineData(" scope ", " value ", true)]
    [InlineData(null, "value", false)]
    [InlineData("scope", null, false)]
    [InlineData(" ", "value", false)]
    [InlineData("scope", "bad\rvalue", false)]
    public void IdempotencyKeyTryCreateReportsValidity(string? scope, string? value, bool expected)
    {
        var success = IdempotencyKey.TryCreate(scope, value, out var key);

        Assert.Equal(expected, success);
        if (expected)
        {
            Assert.Equal(scope!.Trim(), key.Scope);
            Assert.Equal(value!.Trim(), key.Value);
        }
        else
        {
            Assert.Equal(default, key);
        }
    }

    [Theory]
    [InlineData(-1d, 10d, 2d, 0d, "initialDelay")]
    [InlineData(10d, 5d, 2d, 0d, "maximumDelay")]
    [InlineData(10d, 20d, 0.5d, 0d, "multiplier")]
    [InlineData(10d, 20d, 2d, -0.1d, "jitterRatio")]
    [InlineData(10d, 20d, 2d, 1.1d, "jitterRatio")]
    public void BackoffConstructorRejectsInvalidConfiguration(
        double initialMilliseconds,
        double maximumMilliseconds,
        double multiplier,
        double jitter,
        string parameterName)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoffPolicy(
            TimeSpan.FromMilliseconds(initialMilliseconds),
            TimeSpan.FromMilliseconds(maximumMilliseconds),
            multiplier,
            jitter));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void BackoffConstructorRejectsNonFiniteValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoffPolicy(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExponentialBackoffPolicy(
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 2, double.PositiveInfinity));
    }

    [Fact]
    public void BackoffWithoutJitterIsExponentialAndCapped()
    {
        var policy = new ExponentialBackoffPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            multiplier: 2,
            jitterRatio: 0);
        var id = Guid.NewGuid();

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(1, id));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(2, id));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(3, id));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(4, id));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.GetDelay(int.MaxValue, id));
    }

    [Fact]
    public void BackoffJitterIsDeterministicPositiveAndBounded()
    {
        var policy = new ExponentialBackoffPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10),
            multiplier: 2,
            jitterRatio: 0.25);
        var id = Guid.Parse("8ff6d82c-b592-4d21-b48b-d0c0ab5cf176");

        var first = policy.GetDelay(2, id);
        var second = policy.GetDelay(2, id);

        Assert.Equal(first, second);
        Assert.InRange(first, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5));
        Assert.InRange(policy.GetDelay(100, id), TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void BackoffRejectsInvalidAttemptAndMessageId()
    {
        var policy = new ExponentialBackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.GetDelay(0, Guid.NewGuid()));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.GetDelay(1, Guid.Empty));
    }
}
