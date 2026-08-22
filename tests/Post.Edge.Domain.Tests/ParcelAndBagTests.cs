using Post.Edge.Domain.Bags;
using Post.Edge.Domain.Common;
using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Domain.Tests;

public sealed class ParcelAndBagTests
{
    private const string BarcodeValue = "123450000000005954400001";

    [Fact]
    public void ParcelFollowsSuccessfulLifecycleAndBecomesImmutable()
    {
        var parcel = CreateParcel();

        parcel.AssignDestination(PostalCenterCode.Create("123"), "C1");
        parcel.MarkExportRegistered();
        parcel.MarkBagged("BAG-1");
        parcel.MarkCoreSynced();

        Assert.Equal(EdgeParcelState.Bagged, parcel.State);
        Assert.Equal("00123", parcel.OperationalDestinationCode);
        Assert.Equal("C1", parcel.ChuteId);
        Assert.Equal("BAG-1", parcel.BagBarcode);
        Assert.True(parcel.IsImmutable);
        Assert.True(parcel.CoreSynced);
        Assert.Throws<DomainException>(() => parcel.AssignDestination(PostalCenterCode.Create("2"), null));
    }

    [Fact]
    public void ParcelResolvesOrFailsPendingState()
    {
        var resolved = CreateParcel(pending: true);
        var failed = CreateParcel(pending: true);

        resolved.ResolvePending();
        failed.FailPending(new ParcelError("E", "failed"));

        Assert.Equal(EdgeParcelState.Registered, resolved.State);
        Assert.Equal(EdgeParcelState.Unresolved, failed.State);
        Assert.Single(failed.Errors);
    }

    [Fact]
    public void ParcelRejectsCoreInboundDecisionOnlyBeforeDestinationAssignment()
    {
        var registered = CreateParcel();
        var pending = CreateParcel(pending: true);
        var error = new ParcelError("CORE_REJECTED", "Core rejected the inbound decision.");

        registered.RejectInboundDecision(error);
        pending.RejectInboundDecision(error);

        Assert.Equal(EdgeParcelState.Unresolved, registered.State);
        Assert.Equal(EdgeParcelState.Unresolved, pending.State);
        Assert.Equal(error, Assert.Single(registered.Errors));
        Assert.Throws<ArgumentNullException>(() => CreateParcel().RejectInboundDecision(null!));
        var assigned = CreateParcel();
        assigned.AssignDestination(PostalCenterCode.Create("123"), null);
        Assert.Throws<DomainException>(() => assigned.RejectInboundDecision(error));
    }

    [Fact]
    public void ParcelRecordsRepeatedDeferredFailuresAndCanBecomeUnresolved()
    {
        var parcel = CreateParcel();
        parcel.AssignDestination(PostalCenterCode.Create("1"), null);
        parcel.MarkDeferred(new ParcelError("E1", "first"));
        parcel.RecordDeferredFailure(new ParcelError("E2", "second"));
        parcel.MarkUnresolved();

        Assert.Equal(EdgeParcelState.Unresolved, parcel.State);
        Assert.Equal(2, parcel.Errors.Count);
    }

    [Fact]
    public void ParcelDestinationReassignmentClearsStaleChute()
    {
        var parcel = CreateParcel();
        parcel.AssignDestination(PostalCenterCode.Create("1"), "A");
        parcel.AssignDestination(PostalCenterCode.Create("1"), null);
        Assert.Equal("A", parcel.ChuteId);

        parcel.AssignDestination(PostalCenterCode.Create("2"), null);
        Assert.Null(parcel.ChuteId);
    }

    [Fact]
    public void FinalBagRequiresPostalBarcodeDeviceLabelAndExactSuccessfulMembers()
    {
        var counts = new BagCounts(2, 1, 1, 0);
        var bag = EdgeBag.Create(
            "BAG-1", Guid.NewGuid(), "D1", PostalCenterCode.Create("2"),
            ["P1", "P2"], [1], "S1", "Road", counts, [new ParcelError("E", "failed")], DateTimeOffset.UtcNow);

        Assert.Equal("D1", bag.DeviceId);
        Assert.Equal(2, bag.MemberBarcodes.Count);
        Assert.Throws<DomainException>(() => EdgeBag.Create(
            "BAG-2", Guid.NewGuid(), "D1", PostalCenterCode.Create("2"),
            ["P1"], [1], "S1", "Road", counts, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FinalBagDispatchAssignmentIsIdempotentButCannotChange()
    {
        var bag = EdgeBag.Create(
            "BAG-1", Guid.NewGuid(), "D1", PostalCenterCode.Create("2"),
            ["P1"], [1], "S1", "Road", new BagCounts(1, 0, 0, 0), null, DateTimeOffset.UtcNow);

        bag.AssignDispatch("DSP-1");
        bag.AssignDispatch("DSP-1");
        bag.MarkCoreConfirmed();

        Assert.True(bag.DispatchAssigned);
        Assert.True(bag.CoreConfirmed);
        Assert.Throws<DomainException>(() => bag.AssignDispatch("DSP-2"));
    }

    [Fact]
    public void PendingClosureTracksRetryCompletionAndTerminalRules()
    {
        var now = DateTimeOffset.UtcNow;
        var closure = PendingBagClosure.Create(
            Guid.NewGuid(), "D1", PostalCenterCode.Create("2"), [Guid.NewGuid()], "S", "Road", now);

        closure.RecordFailedAttempt("offline", now.AddMinutes(1));
        closure.Complete("BAG-1");

        Assert.Equal(1, closure.Attempts);
        Assert.Equal(PendingBagClosureState.Completed, closure.State);
        Assert.Equal("BAG-1", closure.FinalBagBarcode);
        Assert.Throws<DomainException>(() => closure.RecordFailedAttempt("again", now));
    }

    [Fact]
    public void PendingClosureCanBePermanentlyFailed()
    {
        var closure = PendingBagClosure.Create(
            Guid.NewGuid(), "D1", PostalCenterCode.Create("2"), [Guid.NewGuid()], "S", "Road", DateTimeOffset.UtcNow);

        closure.MarkPermanentlyFailed("fatal");

        Assert.Equal(PendingBagClosureState.PermanentlyFailed, closure.State);
        Assert.Null(closure.NextAttemptAt);
    }

    private static EdgeParcel CreateParcel(bool pending = false) => EdgeParcel.Create(
        Guid.NewGuid(),
        BarcodeSet.Create([BarcodeValue]),
        "D1",
        DateTimeOffset.UtcNow,
        new PhysicalAttributes(100, new ParcelDimensions(1, 2, 3)),
        "Parcel",
        "Express",
        pending);
}
