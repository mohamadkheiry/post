using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Contracts;
using Post.Core.DeviceConfig.Api;
using Post.Core.Infrastructure.Persistence;
using Post.Core.ParcelLifecycle.Api;

namespace Post.Api.IntegrationTests;

public sealed class CoreParcelLifecycleHostTests
{
    private const string Barcode = "123456789012345678901234";

    [Fact]
    public async Task ExplicitDevelopmentIdentitySupportsAuthOffEdgeWriteAndScopedAdminRead()
    {
        var edgeId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        using var factory = new CoreApiFactory<ParcelLifecycleApiMarker>(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Api:AuthenticationEnabled"] = "false",
                ["Api:DevelopmentIdentity:Enabled"] = "true",
                ["Api:DevelopmentIdentity:EdgeId"] = edgeId.ToString("D"),
                ["Api:DevelopmentIdentity:ExchangeCenterCode"] = "12345"
            });
        using var client = factory.CreateClient();
        var inbound = new InboundQueryRequest(
            "local-inbound",
            [Barcode],
            "Parcel",
            "Express",
            "12345",
            "54321",
            Guid.NewGuid(),
            "12345",
            new PhysicalAttributesDto(1, 2, 3, 4),
            DateTimeOffset.UtcNow,
            null);

        using (var request = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post,
                   "/api/edge/parcels/inbound-query",
                   inbound,
                   idempotencyKey: "local-inbound"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using var adminResponse = await client.GetAsync(new Uri($"/api/admin/parcels/{Barcode}", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
    }

    [Fact]
    public async Task EdgeWritesAreScopedIdempotentAndKeepEdgeIdentityDistinctFromSortingDevice()
    {
        using var factory = new CoreApiFactory<ParcelLifecycleApiMarker>();
        using var client = factory.CreateClient();
        var edgeId = Guid.NewGuid();
        var sortingDeviceId = Guid.NewGuid();
        var edgeToken = CoreApiTestClient.EdgeToken(edgeId, "C1");
        var occurredAt = DateTimeOffset.UtcNow;
        var inbound = new InboundQueryRequest(
            "inbound-one",
            [Barcode],
            "Parcel",
            "Express",
            "C1",
            "C2",
            sortingDeviceId,
            "C1",
            new PhysicalAttributesDto(1.5m, 10m, 20m, 30m),
            occurredAt,
            "business-correlation");

        using var health = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var anonymousRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/parcels/inbound-query", inbound, idempotencyKey: "inbound-one");
        using var anonymous = await client.SendAsync(anonymousRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var wrongRoleRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/parcels/inbound-query",
            inbound,
            CoreApiTestClient.AdministratorToken(),
            "inbound-one");
        using var wrongRole = await client.SendAsync(wrongRoleRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);

        using var wrongCenterRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/parcels/inbound-query",
            inbound,
            CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C9"),
            "inbound-one");
        using var wrongCenter = await client.SendAsync(wrongCenterRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrongCenter.StatusCode);

        using var multiCenterRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/parcels/inbound-query",
            inbound,
            CoreApiTestClient.EdgeToken(edgeId, "C1", "C2"),
            "inbound-one");
        using var multiCenter = await client.SendAsync(multiCenterRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, multiCenter.StatusCode);

        using var firstRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/parcels/inbound-query", inbound, edgeToken, "inbound-one", "wire-one");
        using var first = await client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstResult = await CoreApiTestClient.ReadAsync<InboundQueryResult>(first);
        Assert.False(firstResult.IsReplay);
        Assert.Equal(Barcode, firstResult.ParcelBarcode);

        using var replayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/parcels/inbound-query", inbound, edgeToken, "inbound-one", "wire-retry");
        using var replay = await client.SendAsync(replayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await CoreApiTestClient.ReadAsync<InboundQueryResult>(replay)).IsReplay);

        using var conflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/parcels/inbound-query",
            inbound with { ServiceType = "Changed" },
            edgeToken,
            "inbound-one");
        using var conflict = await client.SendAsync(conflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var bag = new BagRequest(
            "BAG-1", "C1", "C2", sortingDeviceId, [Barcode], "SEAL-1", "Road", occurredAt,
            "bag-one", "business-correlation");
        using var bagRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/bags", bag, edgeToken, "bag-one");
        using var bagResponse = await client.SendAsync(bagRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, bagResponse.StatusCode);

        using var bagReplayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/bags", bag, edgeToken, "bag-one", "wire-retry");
        using var bagReplayResponse = await client.SendAsync(bagReplayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, bagReplayResponse.StatusCode);
        Assert.True((await CoreApiTestClient.ReadAsync<StorageResult>(bagReplayResponse)).IsReplay);

        using var bagConflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/bags", bag with { SealNumber = "SEAL-2" }, edgeToken, "bag-one");
        using var bagConflict = await client.SendAsync(bagConflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, bagConflict.StatusCode);

        using var bagKeyReuseRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/bags",
            bag with { BagBarcode = "BAG-OTHER" },
            edgeToken,
            "bag-one");
        using var bagKeyReuse = await client.SendAsync(bagKeyReuseRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, bagKeyReuse.StatusCode);

        var dispatch = new DispatchRequest("DISPATCH-1", "C2", ["BAG-1"], occurredAt, "dispatch-one", null);
        using var destinationCenterDispatchRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/dispatches",
            dispatch,
            CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C2"),
            "dispatch-one");
        using var destinationCenterDispatch = await client.SendAsync(destinationCenterDispatchRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, destinationCenterDispatch.StatusCode);

        using var privilegedDestinationRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/dispatches",
            dispatch,
            CoreApiTestClient.EdgeTokenWithRoles(
                Guid.NewGuid(),
                ["C2"],
                "SystemAdministrator"),
            "dispatch-one");
        using var privilegedDestination = await client.SendAsync(privilegedDestinationRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, privilegedDestination.StatusCode);

        using var dispatchRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/dispatches", dispatch, edgeToken, "dispatch-one");
        using var dispatchResponse = await client.SendAsync(dispatchRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, dispatchResponse.StatusCode);

        using var dispatchReplayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/dispatches", dispatch, edgeToken, "dispatch-one");
        using var dispatchReplayResponse = await client.SendAsync(dispatchReplayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, dispatchReplayResponse.StatusCode);
        Assert.True((await CoreApiTestClient.ReadAsync<StorageResult>(dispatchReplayResponse)).IsReplay);

        using var dispatchKeyReuseRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/dispatches",
            dispatch with { DispatchId = "DISPATCH-OTHER" },
            edgeToken,
            "dispatch-one");
        using var dispatchKeyReuse = await client.SendAsync(dispatchKeyReuseRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, dispatchKeyReuse.StatusCode);

        using var destinationCenterReplayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/dispatches",
            dispatch,
            CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C2"),
            "dispatch-one");
        using var destinationCenterReplay = await client.SendAsync(destinationCenterReplayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, destinationCenterReplay.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ParcelLifecycleDbContext>();
        Assert.Single(database.ReadingRecords);
        Assert.Single(database.Bags);
        Assert.Single(database.Dispatches);
    }

    [Fact]
    public async Task BagRejectsMemberWithoutReadingOwnedByItsOriginCenter()
    {
        const string crossCenterBarcode = "223456789012345678901234";
        using var factory = new CoreApiFactory<ParcelLifecycleApiMarker>();
        using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var inbound = new InboundQueryRequest(
            "cross-center-reading",
            [crossCenterBarcode],
            "Parcel",
            "Express",
            "C1",
            "C3",
            Guid.NewGuid(),
            "C2",
            new PhysicalAttributesDto(1m, 2m, 3m, 4m),
            now,
            null);
        using (var inboundRequest = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post,
                   "/api/edge/parcels/inbound-query",
                   inbound,
                   CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C2"),
                   "cross-center-reading"))
        using (var inboundResponse = await client.SendAsync(inboundRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, inboundResponse.StatusCode);
        }

        var bag = new BagRequest(
            "BAG-CROSS",
            "C1",
            "C3",
            Guid.NewGuid(),
            [crossCenterBarcode],
            "SEAL",
            "Road",
            now,
            "cross-center-bag",
            null);
        using var bagRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/bags",
            bag,
            CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"),
            "cross-center-bag");
        using var bagResponse = await client.SendAsync(bagRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, bagResponse.StatusCode);
    }

    [Fact]
    public async Task AdministratorQueriesApplyCenterScopeToParcelsBagsAndDispatches()
    {
        using var factory = new CoreApiFactory<ParcelLifecycleApiMarker>();
        using var client = factory.CreateClient();
        var sortingDeviceId = Guid.NewGuid();
        var edgeToken = CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1");
        var now = DateTimeOffset.UtcNow;
        var inbound = new InboundQueryRequest(
            "query-seed", [Barcode], "Parcel", "Express", "C1", "C2", sortingDeviceId, "C1",
            new PhysicalAttributesDto(1m, 2m, 3m, 4m), now, null);
        using (var request = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post, "/api/edge/parcels/inbound-query", inbound, edgeToken, "query-seed"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var bag = new BagRequest("BAG-Q", "C1", "C2", sortingDeviceId, [Barcode], "S", "Road", now, "bag-q", null);
        using (var request = CoreApiTestClient.JsonRequest(HttpMethod.Post, "/api/edge/bags", bag, edgeToken, "bag-q"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var dispatch = new DispatchRequest("DISPATCH-Q", "C2", ["BAG-Q"], now, "dispatch-q", null);
        using (var request = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post, "/api/edge/dispatches", dispatch, edgeToken, "dispatch-q"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var centerOneAdmin = CoreApiTestClient.AdministratorToken("CenterAdministrator", "C1");
        using var parcelRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get, $"/api/admin/parcels/{Barcode}", token: centerOneAdmin);
        using var parcel = await client.SendAsync(parcelRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, parcel.StatusCode);

        using var parcelPageRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            $"/api/admin/parcels?barcode={Barcode[..8]}&page=1&pageSize=1",
            token: centerOneAdmin);
        using var parcelPageResponse = await client.SendAsync(parcelPageRequest, TestContext.Current.CancellationToken);
        var parcelPage = await CoreApiTestClient.ReadAsync<
            Post.Core.ParcelLifecycle.Api.PagedResponse<ParcelTrackingDto>>(parcelPageResponse);
        Assert.Equal(1, parcelPage.TotalCount);
        Assert.Equal(Barcode, Assert.Single(parcelPage.Items).ParcelBarcode);

        using var bagRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get, "/api/admin/bags/BAG-Q", token: centerOneAdmin);
        using var bagResponse = await client.SendAsync(bagRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, bagResponse.StatusCode);

        using var scopedDispatchRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get, "/api/admin/dispatches/DISPATCH-Q", token: centerOneAdmin);
        using var scopedDispatch = await client.SendAsync(scopedDispatchRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, scopedDispatch.StatusCode);

        using var globalDispatchRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/admin/dispatches/DISPATCH-Q",
            token: CoreApiTestClient.AdministratorToken());
        using var globalDispatch = await client.SendAsync(globalDispatchRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, globalDispatch.StatusCode);

        using var isolatedRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            $"/api/admin/parcels/{Barcode}",
            token: CoreApiTestClient.AdministratorToken("CenterAdministrator", "C9"));
        using var isolated = await client.SendAsync(isolatedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, isolated.StatusCode);
    }
}

public sealed class CoreDeviceConfigHostTests
{
    [Fact]
    public async Task AdministratorMutationsAreScopedAndDurablyIdempotent()
    {
        using var factory = new CoreApiFactory<DeviceConfigApiMarker>();
        using var client = factory.CreateClient();
        var admin = CoreApiTestClient.AdministratorToken("CenterAdministrator", "C1");
        var request = new RegisterDeviceRequest("Sorter", "AutomaticSorter", "SORT-1", "Post", "C1", "primary");

        using var health = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var anonymousRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/devices", request, idempotencyKey: "register-one");
        using var anonymous = await client.SendAsync(anonymousRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var edgeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/admin/devices",
            request,
            CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"),
            "register-one");
        using var edge = await client.SendAsync(edgeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, edge.StatusCode);

        using var crossCenterRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/admin/devices",
            request,
            CoreApiTestClient.AdministratorToken("CenterAdministrator", "C2"),
            "register-one");
        using var crossCenter = await client.SendAsync(crossCenterRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossCenter.StatusCode);

        using var firstRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/devices", request, admin, "register-one");
        using var first = await client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var registered = await CoreApiTestClient.ReadAsync<RegisteredDeviceResult>(first);
        var initialSecret = Assert.IsType<string>(registered.DeviceSecret);

        using var pageRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/admin/devices?exchangeCenterCode=C1&page=1&pageSize=1",
            token: admin);
        using var pageResponse = await client.SendAsync(pageRequest, TestContext.Current.CancellationToken);
        var page = await CoreApiTestClient.ReadAsync<
            Post.Core.DeviceConfig.Api.PagedResponse<DeviceDto>>(pageResponse);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(registered.DeviceId, Assert.Single(page.Items).DeviceId);

        using var invalidPageRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/admin/devices?page=0&pageSize=1",
            token: admin);
        using var invalidPage = await client.SendAsync(invalidPageRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);

        using var replayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/devices", request, admin, "register-one", "retry-corr");
        using var replay = await client.SendAsync(replayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await CoreApiTestClient.ReadAsync<RegisteredDeviceResult>(replay);
        Assert.True(replayed.IsReplay);
        Assert.True(replayed.RecoveryRequired);
        Assert.Null(replayed.DeviceSecret);

        using var conflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/devices", request with { Name = "Different" }, admin, "register-one");
        using var conflict = await client.SendAsync(conflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var update = new UpdateDeviceRequest("Updated", "AutomaticSorter", "Post", "updated");
        using (var updateRequest = CoreApiTestClient.JsonRequest(
                   HttpMethod.Put, $"/api/admin/devices/{registered.DeviceId:D}", update, admin, "update-one"))
        using (var updateResponse = await client.SendAsync(updateRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);
        }

        using (var updateReplayRequest = CoreApiTestClient.JsonRequest(
                   HttpMethod.Put, $"/api/admin/devices/{registered.DeviceId:D}", update, admin, "update-one"))
        using (var updateReplayResponse = await client.SendAsync(updateReplayRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, updateReplayResponse.StatusCode);
        }

        using var updateConflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Put,
            $"/api/admin/devices/{registered.DeviceId:D}",
            update with { Name = "Conflicting" },
            admin,
            "update-one");
        using var updateConflict = await client.SendAsync(updateConflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, updateConflict.StatusCode);

        var publish = new PublishConfigurationRequest(
            "C1",
            ["100"],
            60,
            120,
            1m,
            2m,
            AutoDispatchEnabled: true,
            MaximumOpenBagMinutes: 90);
        using var publishRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/config", publish, admin, "config-one");
        using var publishedResponse = await client.SendAsync(publishRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, publishedResponse.StatusCode);
        var published = await CoreApiTestClient.ReadAsync<ConfigurationSnapshotDto>(publishedResponse);
        var verifier = Assert.Single(published.Devices);
        Assert.DoesNotContain(initialSecret, verifier.SecretHash, StringComparison.Ordinal);
        Assert.True(published.AutoDispatchEnabled);
        Assert.Equal(90, published.MaximumOpenBagMinutes);

        using (var configurationRequest = CoreApiTestClient.JsonRequest(
                   HttpMethod.Get, "/api/admin/config?exchangeCenterCode=C1", token: admin))
        using (var configurationResponse = await client.SendAsync(
                   configurationRequest,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, configurationResponse.StatusCode);
            var current = await CoreApiTestClient.ReadAsync<ConfigurationSnapshotDto>(configurationResponse);
            Assert.Equal(published.Version, current.Version);
        }

        using var publishReplayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/config", publish, admin, "config-one", "retry-corr");
        using var publishReplayResponse = await client.SendAsync(publishReplayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, publishReplayResponse.StatusCode);
        Assert.Equal(published.Version,
            (await CoreApiTestClient.ReadAsync<ConfigurationSnapshotDto>(publishReplayResponse)).Version);

        using var publishConflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/config", publish with { WeightToleranceKg = 3m }, admin, "config-one");
        using var publishConflict = await client.SendAsync(publishConflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, publishConflict.StatusCode);

        using var activationRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Patch,
            $"/api/admin/devices/{registered.DeviceId:D}",
            new DeviceActivationRequest(false),
            admin,
            "activation-one");
        using var activation = await client.SendAsync(activationRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, activation.StatusCode);

        using var activationReplayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Patch,
            $"/api/admin/devices/{registered.DeviceId:D}",
            new DeviceActivationRequest(false),
            admin,
            "activation-one");
        using var activationReplay = await client.SendAsync(activationReplayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, activationReplay.StatusCode);

        using var activationConflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Patch,
            $"/api/admin/devices/{registered.DeviceId:D}",
            new DeviceActivationRequest(true),
            admin,
            "activation-one");
        using var activationConflict = await client.SendAsync(activationConflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, activationConflict.StatusCode);

        using var rotateRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            $"/api/admin/devices/{registered.DeviceId:D}/rotate-secret",
            token: admin,
            idempotencyKey: "rotate-one");
        using var rotatedResponse = await client.SendAsync(rotateRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, rotatedResponse.StatusCode);
        var rotated = await CoreApiTestClient.ReadAsync<RegisteredDeviceResult>(rotatedResponse);
        var replacementSecret = Assert.IsType<string>(rotated.DeviceSecret);
        Assert.NotEqual(initialSecret, replacementSecret);

        using var rotateReplayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            $"/api/admin/devices/{registered.DeviceId:D}/rotate-secret",
            token: admin,
            idempotencyKey: "rotate-one");
        using var rotateReplayResponse = await client.SendAsync(rotateReplayRequest, TestContext.Current.CancellationToken);
        var rotateReplay = await CoreApiTestClient.ReadAsync<RegisteredDeviceResult>(rotateReplayResponse);
        Assert.True(rotateReplay.IsReplay);
        Assert.Null(rotateReplay.DeviceSecret);

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDeviceConfigStore>();
        var persisted = await store.FindDeviceAsync(new Post.Core.Domain.ValueObjects.DeviceId(registered.DeviceId), TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        var hasher = scope.ServiceProvider.GetRequiredService<ISecretHasher>();
        Assert.True(hasher.Verify(initialSecret, persisted.PreviousSecretVerifier!));
        Assert.True(hasher.Verify(replacementSecret, persisted.SecretVerifier));
        using (var bootstrapRequest = CoreApiTestClient.JsonRequest(
                   HttpMethod.Get,
                   "/api/edge/config/bootstrap",
                   token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1")))
        using (var bootstrapResponse = await client.SendAsync(bootstrapRequest, TestContext.Current.CancellationToken))
        {
            var bootstrap = await CoreApiTestClient.ReadAsync<ConfigurationSnapshotDto>(bootstrapResponse);
            var bootstrapDevice = Assert.Single(bootstrap.Devices);
            Assert.Equal(verifier.SecretHash, bootstrapDevice.PreviousSecretHash);
            Assert.NotNull(bootstrapDevice.PreviousSecretValidUntil);
        }
        var database = scope.ServiceProvider.GetRequiredService<DeviceConfigDbContext>();
        Assert.DoesNotContain(database.IdempotencyRecords,
            x => x.ResponseSnapshot.Contains(initialSecret, StringComparison.Ordinal) ||
                 x.ResponseSnapshot.Contains(replacementSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(database.OutboxMessages,
            x => x.Payload.Contains(initialSecret, StringComparison.Ordinal) ||
                 x.Payload.Contains(replacementSecret, StringComparison.Ordinal));
        Assert.DoesNotContain(factory.LogMessages,
            message => message.Contains(initialSecret, StringComparison.Ordinal) ||
                       message.Contains(replacementSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BootstrapRequiresEdgeAuthenticationAndExactCenterScope()
    {
        using var factory = new CoreApiFactory<DeviceConfigApiMarker>();
        using var client = factory.CreateClient();
        var admin = CoreApiTestClient.AdministratorToken("CenterAdministrator", "C1");
        var register = new RegisterDeviceRequest("Sorter", "Sorter", "SORT-B", "Post", "C1", null);
        using (var request = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post, "/api/admin/devices", register, admin, "bootstrap-device"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var publish = new PublishConfigurationRequest("C1", ["100"], 60, 120, 1m, 2m);
        using (var request = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post, "/api/admin/config", publish, admin, "bootstrap-config"))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        using var anonymous = await client.GetAsync(new Uri("/api/edge/config/bootstrap?exchangeCenterCode=C1", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var crossRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/edge/config/bootstrap?exchangeCenterCode=C1",
            token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C2"));
        using var cross = await client.SendAsync(crossRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);

        using var multiCenterRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/edge/config/bootstrap?exchangeCenterCode=C1",
            token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1", "C2"));
        using var multiCenter = await client.SendAsync(multiCenterRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, multiCenter.StatusCode);

        using var matchingRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/edge/config/bootstrap",
            token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"));
        using var matching = await client.SendAsync(matchingRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, matching.StatusCode);
        var snapshot = await CoreApiTestClient.ReadAsync<ConfigurationSnapshotDto>(matching);
        Assert.Equal("C1", snapshot.ExchangeCenterCode);
        var etag = Assert.Single(matching.Headers.GetValues("ETag"));
        Assert.StartsWith("\"cfg-", etag, StringComparison.Ordinal);

        using var unchangedVersionRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            $"/api/edge/config/bootstrap?currentVersion={snapshot.Version}",
            token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"));
        using var unchangedVersion = await client.SendAsync(unchangedVersionRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, unchangedVersion.StatusCode);
        Assert.Empty(await unchangedVersion.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));

        using var etagRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/edge/config/bootstrap?currentVersion=0",
            token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"));
        etagRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var etagResponse = await client.SendAsync(etagRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, etagResponse.StatusCode);

        using var rollbackRecoveryRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            $"/api/edge/config/bootstrap?currentVersion={snapshot.Version + 1}",
            token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"));
        using var rollbackRecovery = await client.SendAsync(rollbackRecoveryRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, rollbackRecovery.StatusCode);

        foreach (var query in new[] { "currentVersion=-1", "currentVersion=not-a-number" })
        {
            using var malformedRequest = CoreApiTestClient.JsonRequest(
                HttpMethod.Get,
                $"/api/edge/config/bootstrap?{query}",
                token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"));
            using var malformed = await client.SendAsync(malformedRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        using var malformedEtagRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/edge/config/bootstrap",
            token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"));
        malformedEtagRequest.Headers.TryAddWithoutValidation("If-None-Match", "not-an-etag");
        using var malformedEtag = await client.SendAsync(malformedEtagRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, malformedEtag.StatusCode);

        using var restartedClient = factory.CreateClient();
        using var restartedRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/edge/config/bootstrap",
            token: CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"));
        restartedRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var restartedResponse = await restartedClient.SendAsync(restartedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, restartedResponse.StatusCode);
    }
}
