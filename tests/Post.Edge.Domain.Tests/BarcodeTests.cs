using Post.Edge.Domain.Common;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Domain.Tests;

public sealed class BarcodeTests
{
    private const string Canonical = "123450000000005954400001";

    [Theory]
    [InlineData("12345678901234", BarcodeKind.FourteenDigit)]
    [InlineData(Canonical, BarcodeKind.TwentyFourDigit)]
    [InlineData(Canonical + "ABCDEFGHIJKLM", BarcodeKind.ThirtySevenCharacterAlias)]
    public void ParseAcceptsSupportedFormats(string value, BarcodeKind expectedKind)
    {
        var barcode = Barcode.Parse(value);

        Assert.Equal(expectedKind, barcode.Kind);
        Assert.Equal(expectedKind == BarcodeKind.ThirtySevenCharacterAlias ? Canonical : value, barcode.Canonical);
    }

    [Fact]
    public void ParseExtractsKnownTwentyFourDigitSegments()
    {
        var barcode = Barcode.Parse(Canonical);

        Assert.Equal("12345", barcode.EmbeddedOriginCode);
        Assert.Equal("59544", barcode.EmbeddedDestinationCode);
        Assert.Equal("01", barcode.EmbeddedServiceCode);
    }

    [Fact]
    public void FourteenDigitBarcodeDoesNotInventEmbeddedOriginOrDestination()
    {
        var barcode = Barcode.Parse("12345678901234");

        Assert.Null(barcode.EmbeddedOriginCode);
        Assert.Null(barcode.EmbeddedDestinationCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("1234567890123A")]
    [InlineData("123450000000005954400001ABCDEFGHIJK M")]
    [InlineData("A23450000000005954400001ABCDEFGHIJKLM")]
    public void ParseRejectsInvalidFormats(string value) =>
        Assert.Throws<DomainException>(() => Barcode.Parse(value));

    [Fact]
    public void BarcodeSetDeduplicatesAliasesAndCanonicalizesThirtySevenCharacters()
    {
        var set = BarcodeSet.Create([Canonical, Canonical + "ABCDEFGHIJKLM", Canonical]);

        Assert.Equal(Canonical, set.CanonicalBarcode.Canonical);
        Assert.Equal(2, set.Aliases.Count);
    }

    [Fact]
    public void BarcodeSetRejectsMixingFourteenDigitWithAnotherType() =>
        Assert.Throws<DomainException>(() => BarcodeSet.Create(["12345678901234", Canonical]));

    [Fact]
    public void BarcodeSetRejectsAliasesWithDifferentCanonicalValues() =>
        Assert.Throws<DomainException>(() => BarcodeSet.Create([
            Canonical,
            "123450000000005954400002ABCDEFGHIJKLM"]));

    [Fact]
    public void BarcodeSetRequiresAtLeastOneBarcode() =>
        Assert.Throws<DomainException>(() => BarcodeSet.Create([]));
}
