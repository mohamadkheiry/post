using System.Net;
using Microsoft.Extensions.Options;
using Post.Contracts;
using Post.Contracts.V1.EdgeCore;
using Post.Core.DataIngestion.Api;
using Post.Core.DeviceConfig.Api;
using Post.Core.EdgeHealth.Api;
using Post.Core.OperationalResult.Api;
using Post.Core.ParcelLifecycle.Api;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Integrations.Core;
using Post.Edge.Infrastructure.Options;

namespace Post.Api.IntegrationTests;

public sealed class EdgeCoreContractHostTests
{
    private const string Barcode = "123456789012345678901234";

    [Fact]
    public async Task EdgeLifecycleMapperIsAcceptedByActualCoreHost()
    {
        using var factory = new CoreApiFactory<ParcelLifecycleApiMarker>();
        using var http = factory.CreateClient();
        var edgeId = Guid.NewGuid();
        var sortingDeviceId = Guid.NewGuid();
        var options = Options.Create(Settings(edgeId, CoreApiTestClient.EdgeToken(edgeId, "C1")));
        using var tokens = new CoreAccessTokenProvider(http, options, new TestClock());
        var client = new CoreApiClient(http, tokens, options);
        var inboundOperation = Guid.NewGuid();

        var inbound = await client.LookupInboundAsync(new CoreInboundLookupRequest(
            inboundOperation,
            sortingDeviceId.ToString("D"),
            [Barcode],
            "Parcel",
            "Express",
            "C1",
            "C2",
            1_000,
            100,
            200,
            300,
            DateTimeOffset.UnixEpoch,
            inboundOperation.ToString("D")), TestContext.Current.CancellationToken);
        Assert.NotEqual(Guid.Empty, inbound.ReadingRecordId);

        var bagOperation = Guid.NewGuid();
        await client.PublishAsync(ContractMessageTypes.EdgeBagClosed, Serialize(new BagClosedV1(
                bagOperation,
                "BAG-EDGE-E2E",
                sortingDeviceId.ToString("D"),
                "C1",
                "C2",
                [Barcode],
                "SEAL-1",
                "Road",
                DateTimeOffset.UnixEpoch,
                new CoreBagClosureCountsV1(1, 0, 0, 0))), "edge-e2e-bag", TestContext.Current.CancellationToken);
        await client.PublishAsync(ContractMessageTypes.EdgeDispatchClosed, Serialize(new DispatchClosedV1(
                Guid.NewGuid(),
                "DISPATCH-EDGE-E2E",
                "C2",
                ["BAG-EDGE-E2E"],
                DateTimeOffset.UnixEpoch,
                false,
                [new CoreDispatchDeviceStatisticsV1(sortingDeviceId.ToString("D"), 1, 1, 0)])), "edge-e2e-dispatch", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EdgeParcelEventMapperIsAcceptedByActualCoreHost()
    {
        using var factory = new CoreApiFactory<DataIngestionApiMarker>();
        using var http = factory.CreateClient();
        var edgeId = Guid.NewGuid();
        var options = Options.Create(Settings(edgeId, CoreApiTestClient.EdgeToken(edgeId, "C1")));
        using var tokens = new CoreAccessTokenProvider(http, options, new TestClock());
        var client = new CoreApiClient(http, tokens, options);
        var operationId = Guid.NewGuid();

        await client.PublishAsync(ContractMessageTypes.EdgeParcelReadingSubmitted, Serialize(new ParcelReadingSubmittedV1(
                operationId,
                Barcode,
                [Barcode],
                Guid.NewGuid().ToString("D"),
                DateTimeOffset.UnixEpoch,
                new CorePhysicalAttributesV1(1_000, 100, 200, 300),
                CoreOperationStatusV1.Succeeded,
                null,
                "C1",
                "C2")), "edge-e2e-parcel-event", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EdgeHealthAndOperationalResultMappersAreAcceptedByActualCoreHosts()
    {
        var edgeId = Guid.NewGuid();
        var token = CoreApiTestClient.EdgeToken(edgeId, "C1");
        using (var healthFactory = new CoreApiFactory<EdgeHealthApiMarker>())
        using (var healthHttp = healthFactory.CreateClient())
        {
            var options = Options.Create(Settings(edgeId, token));
            using var tokens = new CoreAccessTokenProvider(healthHttp, options, new TestClock());
            var client = new CoreApiClient(healthHttp, tokens, options);
            await client.RegisterAsync(new CoreEdgeRegistration(
                edgeId, "C1", "test", 1, DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken);
            await client.SendHeartbeatAsync(new CoreEdgeHeartbeat(
                edgeId, DateTimeOffset.UnixEpoch, "test", 1, 1_000_000, 2, 1, 0,
                DateTimeOffset.UnixEpoch), TestContext.Current.CancellationToken);
        }

        using var operationalFactory = new CoreApiFactory<OperationalResultApiMarker>();
        using var operationalHttp = operationalFactory.CreateClient();
        var operationalOptions = Options.Create(Settings(edgeId, token));
        using var operationalTokens = new CoreAccessTokenProvider(
            operationalHttp, operationalOptions, new TestClock());
        var operationalClient = new CoreApiClient(operationalHttp, operationalTokens, operationalOptions);
        await operationalClient.PublishAsync("OperationalResultCaptured", Serialize(new CoreOperationalResultMessage(
                Guid.NewGuid().ToString("D"),
                Barcode,
                "PostalInbound.Registered",
                true,
                null,
                null,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch,
                1,
                "Succeeded")), "edge-e2e-operational-result", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EdgeConfigurationSourceConsumesActualCoreBootstrap()
    {
        using var factory = new CoreApiFactory<DeviceConfigApiMarker>();
        using var http = factory.CreateClient();
        var admin = CoreApiTestClient.AdministratorToken("CenterAdministrator", "C1");
        using (var register = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post,
                   "/api/admin/devices",
                   new RegisterDeviceRequest("Sorter", "Sorter", "SORT-E2E", "Post", "C1", null),
                   admin,
                   "edge-bootstrap-device"))
        using (var response = await http.SendAsync(register, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using (var publish = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post,
                   "/api/admin/config",
                   new PublishConfigurationRequest("C1", ["C2"], 60, 120, 1, 2),
                   admin,
                   "edge-bootstrap-config"))
        using (var response = await http.SendAsync(publish, TestContext.Current.CancellationToken))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var edgeId = Guid.NewGuid();
        var options = Options.Create(Settings(edgeId, CoreApiTestClient.EdgeToken(edgeId, "C1")));
        using var tokens = new CoreAccessTokenProvider(http, options, new TestClock());
        var source = new CoreConfigurationSource(http, tokens, options);
        var result = Assert.IsType<EdgeConfigurationDto>(await source.FetchAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal("C1", result.CenterCode);
        Assert.Single(result.Devices);
        Assert.DoesNotContain(result.Devices, device => string.IsNullOrWhiteSpace(device.SecretHash));
    }

    private static CoreApiOptions Settings(Guid edgeId, string token) => new()
    {
        UseFixedBearerToken = true,
        BearerToken = token,
        EdgeId = edgeId,
        ExchangeCenterCode = "C1"
    };

    private static string Serialize<T>(T value) =>
        System.Text.Json.JsonSerializer.Serialize(value, CoreApiTestClient.JsonOptions);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
