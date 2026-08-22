using Post.BuildingBlocks.Time;
using Post.BuildingBlocks.Validation;

namespace PostShared.Tests.BuildingBlocks;

public sealed class GuardAndClockTests
{
    [Fact]
    public void NotNullReturnsValueAndRejectsNull()
    {
        var instance = new object();
        Assert.Same(instance, Guard.NotNull(instance, "instance"));
        Assert.Throws<ArgumentNullException>(() => Guard.NotNull<object>(null, "instance"));
    }

    [Fact]
    public void NotNullOrWhiteSpaceTrimsAndRejectsBlank()
    {
        Assert.Equal("value", Guard.NotNullOrWhiteSpace(" value ", "value"));
        Assert.Throws<ArgumentException>(() => Guard.NotNullOrWhiteSpace(" ", "value"));
    }

    [Fact]
    public void NotEmptyReturnsGuidAndRejectsEmpty()
    {
        var value = Guid.NewGuid();
        Assert.Equal(value, Guard.NotEmpty(value, "value"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.NotEmpty(Guid.Empty, "value"));
    }

    [Fact]
    public void NotDefaultReturnsNonDefaultAndRejectsDefault()
    {
        Assert.Equal(1, Guard.NotDefault(1, "value"));
        Assert.Throws<ArgumentException>(() => Guard.NotDefault(0, "value"));
    }

    [Fact]
    public void InRangeAcceptsBoundariesAndRejectsBadRangeOrValue()
    {
        Assert.Equal(1, Guard.InRange(1, 1, 3, "value"));
        Assert.Equal(3, Guard.InRange(3, 1, 3, "value"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.InRange(4, 1, 3, "value"));
        Assert.Throws<ArgumentException>(() => Guard.InRange(2, 3, 1, "value"));
    }

    [Fact]
    public void NumericGuardsCoverZeroAndNegativeBoundaries()
    {
        Assert.Equal(0, Guard.NotNegative(0, "value"));
        Assert.Equal(1, Guard.Positive(1, "value"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.NotNegative(-1, "value"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.Positive(0, "value"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.Positive(-1, "value"));
    }

    [Fact]
    public void AgainstThrowsOnlyForInvalidCondition()
    {
        Guard.Against(false, "message", "value");
        Assert.Throws<ArgumentException>(() => Guard.Against(true, "message", "value"));
    }

    [Fact]
    public void SystemClockIsSingletonAndReturnsCurrentUtcTime()
    {
        var before = DateTimeOffset.UtcNow;
        var actual = SystemClock.Instance.UtcNow;
        var after = DateTimeOffset.UtcNow;

        Assert.Same(SystemClock.Instance, SystemClock.Instance);
        Assert.Equal(TimeSpan.Zero, actual.Offset);
        Assert.InRange(actual, before, after);
    }
}
