using System.Text.Json;
using System.Globalization;
using Post.BuildingBlocks.Serialization;
using Post.Contracts;
using Post.Contracts.V1.Device;

namespace PostShared.Tests.Contracts;

public sealed class DeviceContractTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-15T08:00:00Z", CultureInfo.InvariantCulture);
    private static readonly Guid CorrelationId = Guid.Parse("a1b2c3d4-0001-4a11-9a11-000000000001");

    [Fact]
    public void EnvelopeCreateMatchesProtocolAndKeepsOptionalOperationId()
    {
        var operationId = Guid.NewGuid();
        var payload = new DeviceAuthenticateRequestV1("DEV-1042", "top-secret");

        var envelope = DeviceEnvelopeV1.Create(
            ContractMessageTypes.DeviceAuthenticate,
            CorrelationId,
            Timestamp,
            payload,
            operationId);

        Assert.Equal(ContractVersions.V1, envelope.ProtocolVersion);
        Assert.Equal("auth", envelope.MessageType);
        Assert.Equal(CorrelationId, envelope.CorrelationId);
        Assert.Equal(Timestamp, envelope.Timestamp);
        Assert.Equal(operationId, envelope.OperationId);
        Assert.Same(payload, envelope.Payload);
    }

    [Fact]
    public void EnvelopeCreateRejectsInvalidTransportValues()
    {
        var payload = new DeviceAuthenticateRequestV1("d", "t");

        Assert.Throws<ArgumentException>(() => DeviceEnvelopeV1.Create(" ", CorrelationId, Timestamp, payload));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeviceEnvelopeV1.Create("auth", Guid.Empty, Timestamp, payload));
        Assert.Throws<ArgumentException>(() => DeviceEnvelopeV1.Create("auth", CorrelationId, Timestamp, payload, Guid.Empty));
        Assert.Throws<ArgumentNullException>(() => DeviceEnvelopeV1.Create<string>("auth", CorrelationId, Timestamp, null!));
    }

    [Fact]
    public void AuthContractSerializesExactNumericStatusAndDocumentedPayload()
    {
        var options = PostJsonDefaults.Create();
        var request = DeviceEnvelopeV1.Create(
            ContractMessageTypes.DeviceAuthenticate,
            CorrelationId,
            Timestamp,
            new DeviceAuthenticateRequestV1("DEV-1042", "secret"));
        var response = DeviceEnvelopeV1.Create(
            ContractMessageTypes.DeviceAuthenticate,
            CorrelationId,
            Timestamp,
            new DeviceAuthenticateResponseV1(DeviceStatusCodeV1.Success, Guid.NewGuid(), Timestamp));

        using var requestJson = JsonDocument.Parse(JsonSerializer.Serialize(request, options));
        using var responseJson = JsonDocument.Parse(JsonSerializer.Serialize(response, options));

        Assert.Equal("1.0", requestJson.RootElement.GetProperty("protocolVersion").GetString());
        Assert.Equal("auth", requestJson.RootElement.GetProperty("messageType").GetString());
        Assert.Equal("DEV-1042", requestJson.RootElement.GetProperty("payload").GetProperty("deviceId").GetString());
        Assert.Equal("secret", requestJson.RootElement.GetProperty("payload").GetProperty("deviceToken").GetString());
        Assert.False(requestJson.RootElement.TryGetProperty("operationId", out _));
        Assert.Equal(0, responseJson.RootElement.GetProperty("payload").GetProperty("status").GetInt32());
        Assert.True(responseJson.RootElement.GetProperty("payload").TryGetProperty("edgeServerTime", out _));
    }

    [Fact]
    public void RegisterInboundContractMatchesOptionalNestedProtocolShape()
    {
        using var supplementary = JsonDocument.Parse("{\"appearanceStatus\":\"intact\"}");
        var payload = new RegisterInboundRequestV1(
            ["590009876543211234567890"],
            Timestamp,
            new PhysicalAttributesV1(850, new ParcelDimensionsV1(300, 200, 100)),
            "packet",
            supplementary.RootElement.Clone(),
            [new ParcelInlineImageV1("img-1", "aW1hZ2U=", "image/jpeg", "front")],
            3_000);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, PostJsonDefaults.Create()));
        var physical = json.RootElement.GetProperty("physicalAttributes");

        Assert.Equal(850, physical.GetProperty("weightGrams").GetInt32());
        Assert.Equal(300, physical.GetProperty("dimensions").GetProperty("lengthMm").GetInt32());
        Assert.Equal("intact", json.RootElement.GetProperty("supplementaryData").GetProperty("appearanceStatus").GetString());
        Assert.Equal("aW1hZ2U=", json.RootElement.GetProperty("images")[0].GetProperty("contentBase64").GetString());
        Assert.Equal(3_000, json.RootElement.GetProperty("timeoutMs").GetInt32());
    }

    [Fact]
    public void RegisterInboundResponseSerializesComprehensiveStatusAsNumber()
    {
        var payload = new RegisterInboundResponseV1(
            DeviceStatusCodeV1.Returned,
            "11000",
            "51000",
            new DeviceDiscrepancyV1(true, false, 800, 850),
            "express",
            "returned");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, PostJsonDefaults.Create()));

        Assert.Equal(3, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("express", json.RootElement.GetProperty("serviceType").GetString());
        Assert.True(json.RootElement.GetProperty("discrepancy").GetProperty("weightMismatch").GetBoolean());
    }

    [Fact]
    public void AssignDestinationContractUsesItsDedicatedNumericStatus()
    {
        var request = new AssignDestinationRequestV1("590009876543211234567890", "51000", "CH-07");
        var response = new AssignDestinationResponseV1(DestinationAssignmentStatusCodeV1.Confirmed);
        var options = PostJsonDefaults.Create();

        using var requestJson = JsonDocument.Parse(JsonSerializer.Serialize(request, options));
        using var responseJson = JsonDocument.Parse(JsonSerializer.Serialize(response, options));

        Assert.Equal("51000", requestJson.RootElement.GetProperty("destinationCenterCode").GetString());
        Assert.Equal(1, responseJson.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public void CloseBagRequestSupportsArrayFiltersAndInformationalCount()
    {
        var payload = new CloseBagRequestV1(
            "51000",
            "SL-99213",
            "road",
            ["CH-07", "CH-08"],
            ["packet", "parcel"],
            [1, 2],
            Count: 42,
            LastBarcode: "590009876543211234567890");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, PostJsonDefaults.Create()));

        Assert.Equal(2, json.RootElement.GetProperty("chuteIds").GetArrayLength());
        Assert.Equal(2, json.RootElement.GetProperty("parcelTypes").GetArrayLength());
        Assert.Equal(2, json.RootElement.GetProperty("serviceTypes").GetArrayLength());
        Assert.Equal(42, json.RootElement.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData(BagCloseResultTypeV1.Completed, "Completed")]
    [InlineData(BagCloseResultTypeV1.NoEligibleParcels, "NoEligibleParcels")]
    [InlineData(BagCloseResultTypeV1.AllParcelsFailed, "AllParcelsFailed")]
    [InlineData(BagCloseResultTypeV1.LabelPending, "LabelPending")]
    [InlineData(BagCloseResultTypeV1.Error, "Error")]
    public void CloseBagResultTypeUsesExactDocumentedWireValue(BagCloseResultTypeV1 result, string expected)
    {
        var payload = new CloseBagResponseV1(
            result is BagCloseResultTypeV1.Error ? DeviceStatusCodeV1.Error : DeviceStatusCodeV1.Success,
            result,
            "51000");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, PostJsonDefaults.Create()));

        Assert.Equal(expected, json.RootElement.GetProperty("resultType").GetString());
    }

    [Fact]
    public void CloseBagCountsComputeEffectiveCountAndResponseCarriesPerParcelErrors()
    {
        var counts = new BagClosureCountsV1(12, 3, 2, 1);
        var response = new CloseBagResponseV1(
            DeviceStatusCodeV1.Success,
            BagCloseResultTypeV1.Completed,
            "51000",
            "BAG-1",
            "base64",
            counts,
            [new BagParcelErrorV1("P1", "E1", "failed", "upstream")]);

        Assert.Equal(12, counts.EffectiveCount);
        Assert.Single(response.Errors!);
    }

    [Theory]
    [InlineData(DispatchCloseResultTypeV1.Completed, "Completed")]
    [InlineData(DispatchCloseResultTypeV1.Error, "Error")]
    public void CloseDispatchHasNoQueuedResultAndUsesExactWireValue(DispatchCloseResultTypeV1 result, string expected)
    {
        var payload = new CloseDispatchResponseV1(
            result is DispatchCloseResultTypeV1.Completed ? DeviceStatusCodeV1.Success : DeviceStatusCodeV1.Error,
            result,
            "51000",
            ProblemParcels: [new DispatchProblemParcelV1("P1", "E1", "problem", "B1")]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, PostJsonDefaults.Create()));

        Assert.Equal(expected, json.RootElement.GetProperty("resultType").GetString());
        Assert.Equal("B1", json.RootElement.GetProperty("problemParcels")[0].GetProperty("bagId").GetString());
        Assert.DoesNotContain("Queued", Enum.GetNames<DispatchCloseResultTypeV1>());
    }

    [Fact]
    public void ProtocolErrorMatchesDocumentedFields()
    {
        var error = new DeviceProtocolErrorV1("VALIDATION_ERROR", "invalid", "weightGrams");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(error, PostJsonDefaults.Create()));

        Assert.Equal("VALIDATION_ERROR", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("weightGrams", json.RootElement.GetProperty("details").GetString());
    }
}
