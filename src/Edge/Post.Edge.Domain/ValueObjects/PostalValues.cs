using Post.Edge.Domain.Common;

namespace Post.Edge.Domain.ValueObjects;

public sealed record PostalCenterCode
{
    private PostalCenterCode(string value) => Value = value;

    public string Value { get; }

    public static PostalCenterCode Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException("Postal center code is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length > 5 || normalized.Any(c => c is < '0' or > '9'))
        {
            throw new DomainException("Postal center code must contain one to five digits.");
        }

        normalized = normalized.PadLeft(5, '0');
        if (normalized == "00000")
        {
            throw new DomainException("Postal center code cannot be zero.");
        }

        return new PostalCenterCode(normalized);
    }

    public override string ToString() => Value;
}

public sealed record ParcelDimensions
{
    public ParcelDimensions(int lengthMm, int widthMm, int heightMm)
    {
        if (lengthMm <= 0 || widthMm <= 0 || heightMm <= 0)
        {
            throw new DomainException("All parcel dimensions must be positive.");
        }

        LengthMm = lengthMm;
        WidthMm = widthMm;
        HeightMm = heightMm;
    }

    public int LengthMm { get; }
    public int WidthMm { get; }
    public int HeightMm { get; }
}

public sealed record PhysicalAttributes
{
    public PhysicalAttributes(int? weightGrams, ParcelDimensions? dimensions)
    {
        if (weightGrams is < 0)
        {
            throw new DomainException("Parcel weight cannot be negative.");
        }

        WeightGrams = weightGrams;
        Dimensions = dimensions;
    }

    public int? WeightGrams { get; }
    public ParcelDimensions? Dimensions { get; }
}

public sealed record ParcelError
{
    public ParcelError(string code, string description, string category = "Operational")
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DomainException("Error code is required.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            throw new DomainException("Error description is required.");
        }

        Code = code.Trim();
        Description = description.Trim();
        Category = string.IsNullOrWhiteSpace(category) ? "Operational" : category.Trim();
    }

    public string Code { get; }
    public string Description { get; }
    public string Category { get; }
}

public sealed record BagCounts
{
    public BagCounts(int n, int p, int m, int q)
    {
        if (n < 0 || p < 0 || m < 0 || q < 0)
        {
            throw new DomainException("Bag counters cannot be negative.");
        }

        if (m > n || q > p)
        {
            throw new DomainException("Failed counters must be subsets of their new/deferred totals.");
        }

        N = n;
        P = p;
        M = m;
        Q = q;
    }

    public int N { get; }
    public int P { get; }
    public int M { get; }
    public int Q { get; }
    public int SuccessCount => (N - M) + (P - Q);
    public int TotalCount => N + P;
    public bool HasErrors => M > 0 || Q > 0;
}
