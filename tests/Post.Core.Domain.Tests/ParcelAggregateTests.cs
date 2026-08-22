using Post.Core.Domain.Common;
using Post.Core.Domain.Parcels;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Domain.Tests;

public sealed class ParcelAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParcelAppendsImmutableReadingHistoryAndLinksPreviousReading()
    {
        var parcel = CreateParcel();
        var first = Append(parcel, "key-1", Now);
        var second = Append(parcel, "key-2", Now.AddHours(1));

        Assert.Equal(2, parcel.Readings.Count);
        Assert.Null(first.PreviousReadingId);
        Assert.Equal(first.Id, second.PreviousReadingId);
        Assert.Same(second, parcel.LatestReading(new ExchangeCenterCode("C1")));
        Assert.Throws<DomainRuleException>(() => Append(parcel, "key-1", Now.AddHours(2)));
    }

    [Fact]
    public void ParcelRejectsImplausibleReceiveTimeAndConflictingBag()
    {
        var parcel = CreateParcel();
        Assert.Throws<DomainRuleException>(() => parcel.AppendReading(
            new IdempotencyKey("key"),
            Now,
            DeviceId.New(),
            new ExchangeCenterCode("C1"),
            new PhysicalAttributes(1, 1, 1, 1),
            ReadingClassification.Success,
            Now,
            Now.AddDays(-31)));

        parcel.AssignPhysicalBag(new BagBarcode("B1"));
        parcel.AssignPhysicalBag(new BagBarcode("B1"));
        Assert.Throws<DomainRuleException>(() => parcel.AssignPhysicalBag(new BagBarcode("B2")));
    }

    [Fact]
    public void LatestReadingUsesEventTimeThenReceiveTimeThenStableIdentifier()
    {
        var parcel = CreateParcel();
        var earlierEventReceivedLater = Append(
            parcel,
            "earlier-event",
            Now,
            Now.AddHours(4),
            "C1");
        var laterEvent = Append(
            parcel,
            "later-event",
            Now.AddHours(1),
            Now.AddHours(2),
            "C2");

        Assert.Same(laterEvent, parcel.LatestReading());

        var sameEventReceivedLater = Append(
            parcel,
            "same-event-later-receipt",
            Now.AddHours(1),
            Now.AddHours(3),
            "C1");
        Assert.Same(sameEventReceivedLater, parcel.LatestReading());

        var identifierTie = Append(
            parcel,
            "identifier-tie",
            Now.AddHours(1),
            Now.AddHours(3),
            "C2");
        var expected = sameEventReceivedLater.Id.CompareTo(identifierTie.Id) > 0
            ? sameEventReceivedLater
            : identifierTie;
        Assert.NotEqual(Guid.Empty, earlierEventReceivedLater.Id);
        Assert.Same(expected, parcel.LatestReading());
    }

    [Fact]
    public void ParcelStateMutatorsApplyBusinessState()
    {
        var parcel = CreateParcel();
        parcel.ApplyFinalStatus(CoreParcelStatus.Returned);
        parcel.SetExportStatus(ParcelExportStatus.Exported);
        parcel.SetOperationalDestination(new ExchangeCenterCode("C9"));

        Assert.Equal(CoreParcelStatus.Returned, parcel.InboundFinalStatus);
        Assert.Equal(ParcelExportStatus.Exported, parcel.ExportStatus);
        Assert.Equal("C9", parcel.OperationalDestinationCode?.Value);
    }

    [Fact]
    public void ReadingStoresDiscrepanciesAndRejectsDuplicateObjectAttachment()
    {
        var reading = Append(CreateParcel(), "key", Now);
        reading.AddDiscrepancy(new PhysicalDiscrepancy(PhysicalAttributeKind.Weight, 10, 12));
        var attachment = reading.AddAttachment("images/a.jpg", "image/jpeg", "parcel", Now);

        Assert.Single(reading.Discrepancies);
        Assert.Equal("images/a.jpg", attachment.ObjectKey);
        Assert.Throws<DomainRuleException>(() =>
            reading.AddAttachment("images/a.jpg", "image/jpeg", "parcel", Now));
        Assert.Throws<DomainRuleException>(() =>
            reading.AddAttachment(string.Empty, "image/jpeg", "parcel", Now));
    }

    [Fact]
    public void PendingAttachmentTracksRetrySchedule()
    {
        var pending = PendingAttachment.Create(
            Guid.NewGuid(),
            "request-hash",
            "{}",
            new ParcelBarcode("P1"),
            Guid.NewGuid(),
            "image.jpg",
            "image/jpeg",
            "parcel",
            "corr",
            Now,
            Now);
        pending.ScheduleRetry(Now, TimeSpan.FromMinutes(5), "ReadingNotAvailable");

        Assert.Equal(1, pending.Attempts);
        Assert.Equal(Now.AddMinutes(5), pending.NextAttemptAt);
        Assert.Equal("ReadingNotAvailable", pending.LastError);
        Assert.True(pending.HasSamePayload("request-hash"));
        Assert.False(pending.HasSamePayload("different"));
        Assert.False(pending.IsExpired(Now.AddMinutes(4), TimeSpan.FromMinutes(5)));
        Assert.True(pending.IsExpired(Now.AddMinutes(5), TimeSpan.FromMinutes(5)));
        Assert.Throws<DomainRuleException>(() => pending.ScheduleRetry(Now, TimeSpan.FromTicks(-1)));
        Assert.Throws<DomainRuleException>(() => pending.IsExpired(Now, TimeSpan.Zero));

        pending.MarkDeadLettered(Now.AddMinutes(6), "MaximumAttemptsExceeded");
        pending.MarkDeadLettered(Now.AddMinutes(7), "ignored-replay");
        Assert.Equal(Now.AddMinutes(6), pending.DeadLetteredAt);
        Assert.Equal("MaximumAttemptsExceeded", pending.LastError);
        Assert.Null(pending.NextAttemptAt);
        Assert.Throws<DomainRuleException>(() => pending.ScheduleRetry(Now, TimeSpan.Zero));
    }

    [Fact]
    public void BagDeduplicatesMembersAndAssignmentIsIdempotentButExclusive()
    {
        var bag = Bag.Create(
            new BagBarcode("B1"),
            new ExchangeCenterCode("C1"),
            new ExchangeCenterCode("C2"),
            DeviceId.New(),
            [new ParcelBarcode("P1"), new ParcelBarcode("P1")],
            "seal",
            "road",
            Now,
            "hash");

        Assert.Single(bag.Members);
        Assert.True(bag.IsReplay("hash"));
        Assert.False(bag.IsReplay("other"));
        bag.AssignToDispatch(new DispatchId("D1"));
        bag.AssignToDispatch(new DispatchId("D1"));
        Assert.Equal(BagState.AssignedToDispatch, bag.State);
        Assert.Throws<DomainRuleException>(() => bag.AssignToDispatch(new DispatchId("D2")));
        Assert.Throws<DomainRuleException>(() => Bag.Create(
            new BagBarcode("EMPTY"),
            new ExchangeCenterCode("C1"),
            new ExchangeCenterCode("C2"),
            DeviceId.New(),
            [],
            "seal",
            "road",
            Now,
            "hash"));
    }

    [Fact]
    public void DispatchRequiresBagAndSupportsPayloadReplayComparison()
    {
        var dispatch = Dispatch.Create(
            new DispatchId("D1"),
            new ExchangeCenterCode("C2"),
            [new BagBarcode("B1"), new BagBarcode("B1")],
            Now,
            "hash");

        Assert.Single(dispatch.Bags);
        Assert.True(dispatch.IsReplay("hash"));
        Assert.False(dispatch.IsReplay("different"));
        Assert.Throws<DomainRuleException>(() => Dispatch.Create(
            new DispatchId("D2"),
            new ExchangeCenterCode("C2"),
            [],
            Now,
            "hash"));
    }

    private static ParcelDossier CreateParcel() => ParcelDossier.Create(
        new ParcelBarcode("P1"),
        "parcel",
        "express",
        new ExchangeCenterCode("C1"),
        new ExchangeCenterCode("C2"));

    private static ReadingRecord Append(ParcelDossier parcel, string key, DateTimeOffset timestamp) =>
        Append(parcel, key, timestamp, timestamp.AddSeconds(1), "C1");

    private static ReadingRecord Append(
        ParcelDossier parcel,
        string key,
        DateTimeOffset timestamp,
        DateTimeOffset coreReceiveTime,
        string center) =>
        parcel.AppendReading(
            new IdempotencyKey(key),
            timestamp,
            DeviceId.New(),
            new ExchangeCenterCode(center),
            new PhysicalAttributes(1, 2, 3, 4),
            ReadingClassification.Success,
            timestamp,
            coreReceiveTime);
}
