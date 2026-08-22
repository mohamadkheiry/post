using Post.Edge.Domain.Common;

namespace Post.Edge.Domain.ValueObjects;

public enum BarcodeKind
{
    FourteenDigit = 14,
    TwentyFourDigit = 24,
    ThirtySevenCharacterAlias = 37
}

public sealed record Barcode
{
    private Barcode(string raw, string canonical, BarcodeKind kind)
    {
        Raw = raw;
        Canonical = canonical;
        Kind = kind;
    }

    public string Raw { get; }
    public string Canonical { get; }
    public BarcodeKind Kind { get; }
    public string? EmbeddedOriginCode => Canonical.Length == 24 ? Canonical[..5] : null;
    public string? EmbeddedDestinationCode => Canonical.Length == 24 ? Canonical.Substring(14, 5) : null;
    public string? EmbeddedServiceCode => Canonical.Length == 24 ? Canonical[^2..] : null;

    public static Barcode Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException("Barcode is required.");
        }

        var normalized = value.Trim();
        return normalized.Length switch
        {
            14 when IsAsciiDigits(normalized) => new Barcode(normalized, normalized, BarcodeKind.FourteenDigit),
            24 when IsAsciiDigits(normalized) => new Barcode(normalized, normalized, BarcodeKind.TwentyFourDigit),
            37 when IsAsciiDigits(normalized[..24]) && normalized.All(c => !char.IsWhiteSpace(c))
                => new Barcode(normalized, normalized[..24], BarcodeKind.ThirtySevenCharacterAlias),
            _ => throw new DomainException("Barcode must be a 14-digit value, a 24-digit value, or a 37-character alias whose first 24 characters are digits.")
        };
    }

    private static bool IsAsciiDigits(string value) => value.All(c => c is >= '0' and <= '9');
}

public sealed record BarcodeSet
{
    private BarcodeSet(Barcode canonicalBarcode, IReadOnlyList<string> aliases)
    {
        CanonicalBarcode = canonicalBarcode;
        Aliases = aliases;
    }

    public Barcode CanonicalBarcode { get; }
    public IReadOnlyList<string> Aliases { get; }

    public static BarcodeSet Create(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var parsed = values.Select(Barcode.Parse).ToArray();
        if (parsed.Length == 0)
        {
            throw new DomainException("At least one barcode is required.");
        }

        var containsFourteen = parsed.Any(x => x.Kind == BarcodeKind.FourteenDigit);
        if (containsFourteen && parsed.Any(x => x.Kind != BarcodeKind.FourteenDigit))
        {
            throw new DomainException("A 14-digit barcode cannot be combined with another barcode type.");
        }

        var canonical = parsed[0].Canonical;
        if (parsed.Any(x => !string.Equals(x.Canonical, canonical, StringComparison.Ordinal)))
        {
            throw new DomainException("All barcodes in one read must identify the same parcel.");
        }

        return new BarcodeSet(
            parsed[0].Kind == BarcodeKind.ThirtySevenCharacterAlias ? Barcode.Parse(canonical) : parsed[0],
            parsed.Select(x => x.Raw).Distinct(StringComparer.Ordinal).ToArray());
    }
}
