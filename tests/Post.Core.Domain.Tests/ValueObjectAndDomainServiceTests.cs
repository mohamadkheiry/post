using Post.Core.Domain.Common;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Domain.Tests;

public sealed class ValueObjectAndDomainServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void StringIdentifiersRejectBlankValues(string value)
    {
        Assert.Throws<DomainRuleException>(() => new ParcelBarcode(value));
        Assert.Throws<DomainRuleException>(() => new BagBarcode(value));
        Assert.Throws<DomainRuleException>(() => new DispatchId(value));
        Assert.Throws<DomainRuleException>(() => new ExchangeCenterCode(value));
        Assert.Throws<DomainRuleException>(() => new LogicalDeviceCode(value));
        Assert.Throws<DomainRuleException>(() => new IdempotencyKey(value));
        Assert.Throws<DomainRuleException>(() => new CorrelationId(value));
    }

    [Fact]
    public void StringIdentifiersTrimAndFormatValues()
    {
        Assert.Equal("P", new ParcelBarcode(" P ").ToString());
        Assert.Equal("B", new BagBarcode(" B ").ToString());
        Assert.Equal("D", new DispatchId(" D ").ToString());
        Assert.Equal("C", new ExchangeCenterCode(" C ").ToString());
        Assert.Equal("L", new LogicalDeviceCode(" L ").ToString());
        Assert.Equal("I", new IdempotencyKey(" I ").ToString());
        Assert.Equal("R", new CorrelationId(" R ").ToString());
    }

    [Fact]
    public void GeneratedIdentifiersAreNonEmptyAndConfigurationVersionIncrements()
    {
        var identifier = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        Assert.NotEqual(Guid.Empty, DeviceId.New().Value);
        Assert.NotEqual(Guid.Empty, EdgeId.New().Value);
        Assert.False(string.IsNullOrWhiteSpace(CorrelationId.New().Value));
        Assert.Equal(2, new ConfigurationVersion(1).Next().Value);
        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", new DeviceId(identifier).ToString());
        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", new EdgeId(identifier).ToString());
        Assert.Equal("1234567890", new ConfigurationVersion(1_234_567_890).ToString());
        Assert.Throws<DomainRuleException>(() => new DeviceId(Guid.Empty));
        Assert.Throws<DomainRuleException>(() => new EdgeId(Guid.Empty));
        Assert.Throws<DomainRuleException>(() => new ConfigurationVersion(0));
    }

    [Fact]
    public void PhysicalValuesRejectNegativeInputs()
    {
        Assert.Throws<DomainRuleException>(() => new PhysicalAttributes(-0.01m, 1, 1, 1));
        Assert.Throws<DomainRuleException>(() => new PhysicalAttributes(1, -0.01m, 1, 1));
        Assert.Throws<DomainRuleException>(() => new DiscrepancyTolerance(-0.01m, 0));
        Assert.Throws<DomainRuleException>(() => new DiscrepancyTolerance(0, -0.01m));
    }

    [Fact]
    public void OperationalSettingsRequirePositiveOrderedThresholds()
    {
        var tolerance = new DiscrepancyTolerance(1, 2);
        Assert.Throws<DomainRuleException>(() => new OperationalSettings(TimeSpan.Zero, TimeSpan.FromHours(1), tolerance));
        Assert.Throws<DomainRuleException>(() => new OperationalSettings(TimeSpan.FromHours(2), TimeSpan.FromHours(1), tolerance));

        var settings = new OperationalSettings(TimeSpan.FromHours(6), TimeSpan.FromHours(72), tolerance);
        Assert.Equal(TimeSpan.FromHours(6), settings.ShortReadingThreshold);
        Assert.Same(tolerance, settings.DiscrepancyTolerance);
        Assert.False(settings.AutoDispatchEnabled);
        Assert.Equal(TimeSpan.FromMinutes(60), settings.MaximumOpenBagDuration);

        var automatic = new OperationalSettings(
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(72),
            tolerance,
            autoDispatchEnabled: true,
            maximumOpenBagDuration: TimeSpan.FromMinutes(90));
        Assert.True(automatic.AutoDispatchEnabled);
        Assert.Equal(TimeSpan.FromMinutes(90), automatic.MaximumOpenBagDuration);
        Assert.Throws<DomainRuleException>(() => new OperationalSettings(
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(72),
            tolerance,
            true,
            TimeSpan.FromSeconds(90)));
        Assert.Throws<DomainRuleException>(() => new OperationalSettings(
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(72),
            tolerance,
            true,
            TimeSpan.FromDays(7).Add(TimeSpan.FromMinutes(1))));
    }

    [Fact]
    public void BarcodeValidatorAcceptsEquivalentRepresentationsAndRejectsMixedParcels()
    {
        var validator = new BarcodeValidator(new BarcodeValidationPolicy(
            "^[0-9]{24}(-ALT)?$",
            value => value.Replace("-ALT", string.Empty, StringComparison.Ordinal)));

        var result = validator.Validate(["123456789012345678901234", "123456789012345678901234-ALT"]);
        Assert.Equal("123456789012345678901234", result.PrimaryBarcode.Value);
        Assert.Equal(2, result.Barcodes.Count);
        Assert.Throws<DomainRuleException>(() => validator.Validate(["123456789012345678901234", "999999999999999999999999"]));
        Assert.Throws<DomainRuleException>(() => validator.Validate(["invalid"]));
    }

    [Fact]
    public void DuplicateClassificationUsesApprovedExactBoundaries()
    {
        var classifier = new DuplicateReadingClassifier();
        var settings = new OperationalSettings(
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(72),
            new DiscrepancyTolerance(0, 0));
        var previous = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(ReadingClassification.Success, classifier.Classify(previous.AddHours(5.999), previous, settings));
        Assert.Equal(ReadingClassification.Returned, classifier.Classify(previous.AddHours(6), previous, settings));
        Assert.Equal(ReadingClassification.Returned, classifier.Classify(previous.AddHours(72), previous, settings));
        Assert.Equal(ReadingClassification.Refused, classifier.Classify(previous.AddHours(72).AddTicks(1), previous, settings));
        Assert.Equal(ReadingClassification.Success, classifier.Classify(previous, null, settings));
        Assert.Throws<DomainRuleException>(() => classifier.Classify(previous.AddTicks(-1), previous, settings));
    }

    [Fact]
    public void DiscrepancyCheckerTreatsEqualityToToleranceAsNoDiscrepancy()
    {
        var checker = new DiscrepancyChecker();
        var reference = new PhysicalAttributes(10, 20, 30, 40);
        var atTolerance = new PhysicalAttributes(11, 22, 28, 42);

        Assert.Empty(checker.Compare(reference, atTolerance, new DiscrepancyTolerance(1, 2)));
        var beyond = checker.Compare(reference, new PhysicalAttributes(11.01m, 22.01m, 30, 40), new DiscrepancyTolerance(1, 2));
        Assert.Equal(2, beyond.Count);
        Assert.Contains(beyond, x => x.Attribute == PhysicalAttributeKind.Weight);
        Assert.Contains(beyond, x => x.Attribute == PhysicalAttributeKind.Length);
    }

    [Theory]
    [InlineData(CoreParcelStatus.Success, DeviceParcelStatus.Success)]
    [InlineData(CoreParcelStatus.Error, DeviceParcelStatus.Error)]
    [InlineData(CoreParcelStatus.Returned, DeviceParcelStatus.Returned)]
    [InlineData(CoreParcelStatus.Refused, DeviceParcelStatus.Refused)]
    public void StatusMapperKeepsCoreAndDeviceEnumsSeparate(
        CoreParcelStatus coreStatus,
        DeviceParcelStatus expected)
    {
        var mapper = new CoreDeviceStatusMapper();
        Assert.Equal(expected, mapper.Map(coreStatus));
    }
}
