using System.Text.Json;
using Post.BuildingBlocks.Serialization;
using Post.Contracts;
using Post.Contracts.V1.Device;
using Post.Contracts.V1.EdgeCore;

namespace PostShared.Tests.Contracts;

public sealed class EdgeCoreContractTests
{
    [Fact]
    public void EnvelopeCreateNormalizesTimeAndPreservesAllReplayIdentifiers()
    {
        var messageId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var causationId = Guid.NewGuid();
        var localTime = new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.FromHours(3.5));
        var payload = new EdgeHeartbeatV1(localTime, "1.0.0", 12, 3, 5, 60, 1000);

        var envelope = EdgeCoreEnvelopeV1.Create(
            ContractMessageTypes.EdgeHeartbeat,
            "correlation-1",
            localTime,
            "edge-1",
            "51000",
            42,
            payload,
            operationId,
            causationId,
            messageId);

        Assert.Equal(ContractVersions.V1, envelope.SchemaVersion);
        Assert.Equal(messageId, envelope.MessageId);
        Assert.Equal(operationId, envelope.OperationId);
        Assert.Equal(causationId, envelope.CausationId);
        Assert.Equal(TimeSpan.Zero, envelope.OccurredAtUtc.Offset);
        Assert.Equal(42, envelope.Sequence);
        Assert.Same(payload, envelope.Payload);
    }

    [Fact]
    public void EnvelopeCreateGeneratesMessageIdAndRejectsInvalidValues()
    {
        var now = DateTimeOffset.UtcNow;

        var valid = EdgeCoreEnvelopeV1.Create("type", "corr", now, "edge", "51000", 0, "payload");
        Assert.NotEqual(Guid.Empty, valid.MessageId);
        Assert.Null(valid.OperationId);

        Assert.Throws<ArgumentException>(() => EdgeCoreEnvelopeV1.Create(" ", "corr", now, "edge", "51000", 0, "p"));
        Assert.Throws<ArgumentException>(() => EdgeCoreEnvelopeV1.Create("t", " ", now, "edge", "51000", 0, "p"));
        Assert.Throws<ArgumentException>(() => EdgeCoreEnvelopeV1.Create("t", "c", now, " ", "51000", 0, "p"));
        Assert.Throws<ArgumentException>(() => EdgeCoreEnvelopeV1.Create("t", "c", now, "edge", " ", 0, "p"));
        Assert.Throws<ArgumentOutOfRangeException>(() => EdgeCoreEnvelopeV1.Create("t", "c", now, "edge", "51000", -1, "p"));
        Assert.Throws<ArgumentException>(() => EdgeCoreEnvelopeV1.Create("t", "c", now, "edge", "51000", 0, "p", Guid.Empty));
        Assert.Throws<ArgumentNullException>(() => EdgeCoreEnvelopeV1.Create<string>("t", "c", now, "edge", "51000", 0, null!));
    }

    [Theory]
    [InlineData(DeviceStatusCodeV1.Success, CoreOperationStatusV1.Succeeded)]
    [InlineData(DeviceStatusCodeV1.Pending, CoreOperationStatusV1.Pending)]
    [InlineData(DeviceStatusCodeV1.Error, CoreOperationStatusV1.Failed)]
    [InlineData(DeviceStatusCodeV1.Returned, CoreOperationStatusV1.Succeeded)]
    [InlineData(DeviceStatusCodeV1.Refused, CoreOperationStatusV1.Succeeded)]
    public void DeviceStatusMapsExplicitlyToCoreOperationStatus(DeviceStatusCodeV1 source, CoreOperationStatusV1 expected)
    {
        Assert.Equal(expected, source.ToCoreOperationStatus());
    }

    [Theory]
    [InlineData(DeviceStatusCodeV1.Success, null)]
    [InlineData(DeviceStatusCodeV1.Pending, null)]
    [InlineData(DeviceStatusCodeV1.Error, null)]
    [InlineData(DeviceStatusCodeV1.Returned, CoreReadingClassificationV1.Returned)]
    [InlineData(DeviceStatusCodeV1.Refused, CoreReadingClassificationV1.ReturnToOrigin)]
    public void DeviceStatusMapsClassificationSeparately(
        DeviceStatusCodeV1 source,
        CoreReadingClassificationV1? expected)
    {
        Assert.Equal(expected, source.ToCoreReadingClassification());
    }

    [Theory]
    [InlineData(DestinationAssignmentStatusCodeV1.Confirmed, CoreOperationStatusV1.Succeeded)]
    [InlineData(DestinationAssignmentStatusCodeV1.Error, CoreOperationStatusV1.Failed)]
    public void DestinationStatusMapsExplicitlyToCore(
        DestinationAssignmentStatusCodeV1 source,
        CoreOperationStatusV1 expected)
    {
        Assert.Equal(expected, source.ToCoreOperationStatus());
    }

    [Theory]
    [InlineData(BagCloseResultTypeV1.Completed, CoreOperationStatusV1.Succeeded)]
    [InlineData(BagCloseResultTypeV1.LabelPending, CoreOperationStatusV1.Pending)]
    [InlineData(BagCloseResultTypeV1.NoEligibleParcels, CoreOperationStatusV1.Rejected)]
    [InlineData(BagCloseResultTypeV1.AllParcelsFailed, CoreOperationStatusV1.Rejected)]
    [InlineData(BagCloseResultTypeV1.Error, CoreOperationStatusV1.Failed)]
    public void BagResultMapsExplicitlyToCore(BagCloseResultTypeV1 source, CoreOperationStatusV1 expected)
    {
        Assert.Equal(expected, source.ToCoreOperationStatus());
    }

    [Theory]
    [InlineData(DispatchCloseResultTypeV1.Completed, CoreOperationStatusV1.Succeeded)]
    [InlineData(DispatchCloseResultTypeV1.Error, CoreOperationStatusV1.Failed)]
    public void DispatchResultMapsExplicitlyToCore(DispatchCloseResultTypeV1 source, CoreOperationStatusV1 expected)
    {
        Assert.Equal(expected, source.ToCoreOperationStatus());
    }

    [Fact]
    public void StatusMappersRejectUnknownEnumValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((DeviceStatusCodeV1)99).ToCoreOperationStatus());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((DeviceStatusCodeV1)99).ToCoreReadingClassification());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((DestinationAssignmentStatusCodeV1)99).ToCoreOperationStatus());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((BagCloseResultTypeV1)99).ToCoreOperationStatus());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((DispatchCloseResultTypeV1)99).ToCoreOperationStatus());
    }

    [Fact]
    public void CoreStatusesSerializeAsStableStringsWhileDeviceStatusRemainsNumeric()
    {
        var options = PostJsonDefaults.Create();
        var coreJson = JsonSerializer.Serialize(CoreParcelLifecycleStatusV1.AwaitingUpstream, options);
        var deviceJson = JsonSerializer.Serialize(DeviceStatusCodeV1.Pending, options);

        Assert.Equal("\"awaiting-upstream\"", coreJson);
        Assert.Equal("1", deviceJson);
    }

    [Fact]
    public void ConfigurationSnapshotContainsHashNotRawDeviceToken()
    {
        var snapshot = new ConfigurationSnapshotV1(
            "51000",
            7,
            DateTimeOffset.UtcNow,
            [new DeviceRegistryEntryV1("device-1", "logical-1", "$argon2id$hash", true)],
            new CenterOperationsPolicyV1(true, 60, 6, 72),
            [new PostalPointReferenceV1("51000", "Tabriz", true)]);

        var json = JsonSerializer.Serialize(snapshot, PostJsonDefaults.Create());

        Assert.Contains("deviceTokenHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("deviceToken\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncBatchContractSupportsIndependentAcknowledgements()
    {
        using var payloadDocument = JsonDocument.Parse("{\"barcode\":\"P1\"}");
        var messageId = Guid.NewGuid();
        var item = new EdgeSyncItemV1(
            messageId,
            ContractMessageTypes.EdgeParcelStatusChanged,
            ContractVersions.V1,
            "corr",
            null,
            DateTimeOffset.UtcNow,
            11,
            payloadDocument.RootElement.Clone());
        var request = new EdgeSyncBatchRequestV1(Guid.NewGuid(), "edge", "51000", 5, [item]);
        var response = new EdgeSyncBatchResponseV1(
            request.BatchId,
            DateTimeOffset.UtcNow,
            [new EdgeSyncAcknowledgementV1(messageId, 11, CoreSyncItemStatusV1.Duplicate)]);

        Assert.Single(request.Items);
        Assert.Equal(CoreSyncItemStatusV1.Duplicate, Assert.Single(response.Acknowledgements).Status);
    }
}
