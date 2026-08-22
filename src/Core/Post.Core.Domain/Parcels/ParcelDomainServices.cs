using System.Text.RegularExpressions;
using Post.Core.Domain.Common;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Domain.Parcels;

public sealed record BarcodeValidationPolicy(string Pattern, Func<string, string>? EquivalenceKey = null)
{
    public static BarcodeValidationPolicy Default { get; } = new("^[0-9]{24}$");
}

public sealed record BarcodeValidationResult(
    ParcelBarcode PrimaryBarcode,
    IReadOnlyCollection<ParcelBarcode> Barcodes);

public sealed class BarcodeValidator
{
    private readonly Regex _pattern;
    private readonly Func<string, string> _equivalenceKey;

    public BarcodeValidator(BarcodeValidationPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.Pattern))
        {
            throw new DomainRuleException("Barcode validation pattern is required.");
        }

        _pattern = new Regex(
            policy.Pattern,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(250));
        _equivalenceKey = policy.EquivalenceKey ?? (value => value);
    }

    public BarcodeValidationResult Validate(IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var normalized = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Where(x => _pattern.IsMatch(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new DomainRuleException("At least one valid parcel barcode is required.");
        }

        var keys = normalized.Select(_equivalenceKey).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length != 1)
        {
            throw new DomainRuleException("Multiple barcodes must identify the same parcel.");
        }

        var barcodes = normalized.Select(x => new ParcelBarcode(x)).ToArray();
        return new BarcodeValidationResult(barcodes[0], barcodes);
    }
}

public interface IDuplicateReadingClassifier
{
    ReadingClassification Classify(
        DateTimeOffset currentReadingAt,
        DateTimeOffset? previousReadingAt,
        OperationalSettings settings);
}

public sealed class DuplicateReadingClassifier : IDuplicateReadingClassifier
{
    public ReadingClassification Classify(
        DateTimeOffset currentReadingAt,
        DateTimeOffset? previousReadingAt,
        OperationalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (previousReadingAt is null)
        {
            return ReadingClassification.Success;
        }

        var elapsed = currentReadingAt - previousReadingAt.Value;
        if (elapsed < TimeSpan.Zero)
        {
            throw new DomainRuleException("A reading cannot be classified before its previous reading.");
        }

        // Approved implementation baseline:
        // elapsed < short => Success;
        // short <= elapsed <= return => Returned;
        // elapsed > return => Refused.
        if (elapsed < settings.ShortReadingThreshold)
        {
            return ReadingClassification.Success;
        }

        return elapsed <= settings.ReturnThreshold
            ? ReadingClassification.Returned
            : ReadingClassification.Refused;
    }
}

public interface IDiscrepancyChecker
{
    IReadOnlyCollection<PhysicalDiscrepancy> Compare(
        PhysicalAttributes reference,
        PhysicalAttributes observed,
        DiscrepancyTolerance tolerance);
}

public sealed class DiscrepancyChecker : IDiscrepancyChecker
{
    public IReadOnlyCollection<PhysicalDiscrepancy> Compare(
        PhysicalAttributes reference,
        PhysicalAttributes observed,
        DiscrepancyTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(tolerance);

        var discrepancies = new List<PhysicalDiscrepancy>();
        AddIfBeyondTolerance(
            discrepancies,
            PhysicalAttributeKind.Weight,
            reference.WeightKg,
            observed.WeightKg,
            tolerance.WeightKg);
        AddIfBeyondTolerance(
            discrepancies,
            PhysicalAttributeKind.Length,
            reference.LengthCm,
            observed.LengthCm,
            tolerance.DimensionCm);
        AddIfBeyondTolerance(
            discrepancies,
            PhysicalAttributeKind.Width,
            reference.WidthCm,
            observed.WidthCm,
            tolerance.DimensionCm);
        AddIfBeyondTolerance(
            discrepancies,
            PhysicalAttributeKind.Height,
            reference.HeightCm,
            observed.HeightCm,
            tolerance.DimensionCm);

        return discrepancies.AsReadOnly();
    }

    private static void AddIfBeyondTolerance(
        List<PhysicalDiscrepancy> target,
        PhysicalAttributeKind attribute,
        decimal reference,
        decimal observed,
        decimal tolerance)
    {
        // Equality with the configured tolerance is explicitly not a discrepancy.
        if (Math.Abs(reference - observed) > tolerance)
        {
            target.Add(new PhysicalDiscrepancy(attribute, reference, observed));
        }
    }
}

public interface ICoreDeviceStatusMapper
{
    DeviceParcelStatus Map(CoreParcelStatus status);
}

public sealed class CoreDeviceStatusMapper : ICoreDeviceStatusMapper
{
    public DeviceParcelStatus Map(CoreParcelStatus status) => status switch
    {
        CoreParcelStatus.Success => DeviceParcelStatus.Success,
        CoreParcelStatus.Error => DeviceParcelStatus.Error,
        CoreParcelStatus.Returned => DeviceParcelStatus.Returned,
        CoreParcelStatus.Refused => DeviceParcelStatus.Refused,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown Core status.")
    };
}
