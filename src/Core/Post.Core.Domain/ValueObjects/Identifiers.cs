using Post.Core.Domain.Common;

namespace Post.Core.Domain.ValueObjects;

public readonly record struct ParcelBarcode
{
    public ParcelBarcode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128)
        {
            throw new DomainRuleException("Parcel barcode must contain between 1 and 128 characters.");
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct BagBarcode
{
    public BagBarcode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128)
        {
            throw new DomainRuleException("Bag barcode must contain between 1 and 128 characters.");
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct DispatchId
{
    public DispatchId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128)
        {
            throw new DomainRuleException("Dispatch identifier must contain between 1 and 128 characters.");
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct ExchangeCenterCode
{
    public ExchangeCenterCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 32)
        {
            throw new DomainRuleException("Exchange center code must contain between 1 and 32 characters.");
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct LogicalDeviceCode
{
    public LogicalDeviceCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 64)
        {
            throw new DomainRuleException("Logical device code must contain between 1 and 64 characters.");
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct IdempotencyKey
{
    public IdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128)
        {
            throw new DomainRuleException("Idempotency key must contain between 1 and 128 characters.");
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct CorrelationId
{
    public CorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128)
        {
            throw new DomainRuleException("Correlation identifier must contain between 1 and 128 characters.");
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public static CorrelationId New() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct DeviceId
{
    public DeviceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainRuleException("Device identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static DeviceId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct EdgeId
{
    public EdgeId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new DomainRuleException("Edge identifier cannot be empty.");
        }

        Value = value;
    }

    public Guid Value { get; }

    public static EdgeId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}

public readonly record struct ConfigurationVersion
{
    public ConfigurationVersion(long value)
    {
        if (value <= 0)
        {
            throw new DomainRuleException("Configuration version must be positive.");
        }

        Value = value;
    }

    public long Value { get; }

    public ConfigurationVersion Next() => new(checked(Value + 1));

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record PhysicalAttributes
{
    public PhysicalAttributes(decimal weightKg, decimal lengthCm, decimal widthCm, decimal heightCm)
    {
        if (weightKg < 0 || lengthCm < 0 || widthCm < 0 || heightCm < 0)
        {
            throw new DomainRuleException("Physical measurements cannot be negative.");
        }

        WeightKg = weightKg;
        LengthCm = lengthCm;
        WidthCm = widthCm;
        HeightCm = heightCm;
    }

    public decimal WeightKg { get; }

    public decimal LengthCm { get; }

    public decimal WidthCm { get; }

    public decimal HeightCm { get; }
}

public sealed record DiscrepancyTolerance
{
    public DiscrepancyTolerance(decimal weightKg, decimal dimensionCm)
    {
        if (weightKg < 0 || dimensionCm < 0)
        {
            throw new DomainRuleException("Discrepancy tolerances cannot be negative.");
        }

        WeightKg = weightKg;
        DimensionCm = dimensionCm;
    }

    public decimal WeightKg { get; }

    public decimal DimensionCm { get; }
}

public sealed record OperationalSettings
{
    public static readonly TimeSpan DefaultMaximumOpenBagDuration = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan MinimumOpenBagDuration = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumOpenBagDurationLimit = TimeSpan.FromDays(7);

    private OperationalSettings()
    {
        DiscrepancyTolerance = new DiscrepancyTolerance(0, 0);
        MaximumOpenBagDuration = DefaultMaximumOpenBagDuration;
    }

    public static OperationalSettings Default { get; } = new(
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(72),
        new DiscrepancyTolerance(0, 0),
        autoDispatchEnabled: false,
        maximumOpenBagDuration: DefaultMaximumOpenBagDuration);

    public OperationalSettings(
        TimeSpan shortReadingThreshold,
        TimeSpan returnThreshold,
        DiscrepancyTolerance discrepancyTolerance,
        bool autoDispatchEnabled = false,
        TimeSpan? maximumOpenBagDuration = null)
    {
        ArgumentNullException.ThrowIfNull(discrepancyTolerance);

        if (shortReadingThreshold <= TimeSpan.Zero || returnThreshold < shortReadingThreshold)
        {
            throw new DomainRuleException("Reading thresholds must be positive and ordered.");
        }

        var effectiveMaximumOpenBagDuration = maximumOpenBagDuration ?? DefaultMaximumOpenBagDuration;
        if (effectiveMaximumOpenBagDuration < MinimumOpenBagDuration ||
            effectiveMaximumOpenBagDuration > MaximumOpenBagDurationLimit ||
            effectiveMaximumOpenBagDuration.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new DomainRuleException(
                $"Maximum open-bag duration must use whole minutes between {MinimumOpenBagDuration} and " +
                $"{MaximumOpenBagDurationLimit}.");
        }

        ShortReadingThreshold = shortReadingThreshold;
        ReturnThreshold = returnThreshold;
        DiscrepancyTolerance = discrepancyTolerance;
        AutoDispatchEnabled = autoDispatchEnabled;
        MaximumOpenBagDuration = effectiveMaximumOpenBagDuration;
    }

    public TimeSpan ShortReadingThreshold { get; }

    public TimeSpan ReturnThreshold { get; }

    public DiscrepancyTolerance DiscrepancyTolerance { get; }

    public bool AutoDispatchEnabled { get; }

    public TimeSpan MaximumOpenBagDuration { get; }
}
