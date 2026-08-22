using Post.Edge.Domain.Common;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Domain.Tests;

public sealed class ValueObjectTests
{
    [Theory]
    [InlineData("1", "00001")]
    [InlineData("123", "00123")]
    [InlineData("59544", "59544")]
    public void PostalCenterCodePadsToFiveDigits(string input, string expected)
    {
        var center = PostalCenterCode.Create(input);

        Assert.Equal(expected, center.Value);
        Assert.Equal(expected, center.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("00000")]
    [InlineData("123456")]
    [InlineData("12A")]
    public void PostalCenterCodeRejectsInvalidValues(string input) =>
        Assert.Throws<DomainException>(() => PostalCenterCode.Create(input));

    [Fact]
    public void PhysicalAttributesPreservesValidWeightAndDimensions()
    {
        var dimensions = new ParcelDimensions(10, 20, 30);
        var physical = new PhysicalAttributes(100, dimensions);

        Assert.Equal(100, physical.WeightGrams);
        Assert.Same(dimensions, physical.Dimensions);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void ParcelDimensionsRequirePositiveValues(int length, int width, int height) =>
        Assert.Throws<DomainException>(() => new ParcelDimensions(length, width, height));

    [Fact]
    public void PhysicalAttributesRejectsNegativeWeight() =>
        Assert.Throws<DomainException>(() => new PhysicalAttributes(-1, null));

    [Fact]
    public void ParcelErrorDefaultsCategory()
    {
        var error = new ParcelError(" E1 ", " failed ", " ");

        Assert.Equal("E1", error.Code);
        Assert.Equal("failed", error.Description);
        Assert.Equal("Operational", error.Category);
    }

    [Fact]
    public void BagCountsComputesSubsetCounters()
    {
        var counts = new BagCounts(10, 4, 2, 1);

        Assert.Equal(14, counts.TotalCount);
        Assert.Equal(11, counts.SuccessCount);
        Assert.True(counts.HasErrors);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(1, 0, 2, 0)]
    [InlineData(0, 1, 0, 2)]
    public void BagCountsRejectsInvalidSubsets(int n, int p, int m, int q) =>
        Assert.Throws<DomainException>(() => new BagCounts(n, p, m, q));
}
