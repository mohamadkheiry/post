using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.EdgeHealth.Api;
using Post.Core.Infrastructure.Persistence;
using Post.Core.ObjectStorage.Api;
using Post.Core.OperationalResult.Api;

namespace Post.Api.IntegrationTests;

public sealed class CoreObjectStorageHostTests
{
    [Fact]
    public async Task ExplicitDevelopmentIdentityEnablesAuthOffLocalPresigning()
    {
        var edgeId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        using var factory = new CoreApiFactory<ObjectStorageApiMarker>(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Api:AuthenticationEnabled"] = "false",
                ["Api:DevelopmentIdentity:Enabled"] = "true",
                ["Api:DevelopmentIdentity:EdgeId"] = edgeId.ToString("D"),
                ["Api:DevelopmentIdentity:ExchangeCenterCode"] = "12345"
            });
        using var client = factory.CreateClient();
        var body = new PresignedUrlRequest(
            Guid.NewGuid(), Guid.NewGuid(), "parcel-image", "image/jpeg", 512, 300);

        using var response = await client.SendAsync(CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await CoreApiTestClient.ReadAsync<PresignedUrlResult>(response);
        Assert.Contains(edgeId.ToString("N"), result.ObjectKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresigningIsEdgeOnlyAndCallerCannotChooseOrCrossEdgeObjectNamespace()
    {
        using var factory = new CoreApiFactory<ObjectStorageApiMarker>(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Core:S3:RequireServerSideEncryption"] = "true"
            });
        using var client = factory.CreateClient();
        var edgeId = Guid.NewGuid();
        var body = new PresignedUrlRequest(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "supplementary-data",
            "application/json",
            1234,
            300);

        using var health = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var anonymousRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/images/presigned-url", body);
        using var anonymous = await client.SendAsync(anonymousRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var adminRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body,
            CoreApiTestClient.AdministratorToken());
        using var admin = await client.SendAsync(adminRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);

        using var validRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body,
            CoreApiTestClient.EdgeToken(edgeId, "C1"));
        using var valid = await client.SendAsync(validRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var result = await CoreApiTestClient.ReadAsync<PresignedUrlResult>(valid);
        Assert.StartsWith("edge-uploads/", result.ObjectKey, StringComparison.Ordinal);
        Assert.Contains(edgeId.ToString("N"), result.ObjectKey, StringComparison.Ordinal);
        Assert.DoesNotContain("..", result.ObjectKey, StringComparison.Ordinal);
        Assert.True(result.UploadUrl.IsAbsoluteUri);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.Equal("AES256", result.RequiredHeaders["x-amz-server-side-encryption"]);
        Assert.Contains(
            "x-amz-server-side-encryption",
            Uri.UnescapeDataString(result.UploadUrl.Query),
            StringComparison.Ordinal);

        using var legacyArbitraryKeyRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            new
            {
                body.EventId,
                body.ReadingRecordId,
                body.AttachmentType,
                body.ContentType,
                body.ContentLength,
                body.LifetimeSeconds,
                ObjectKey = "../secret"
            },
            CoreApiTestClient.EdgeToken(edgeId, "C1"));
        using var legacyArbitraryKey = await client.SendAsync(legacyArbitraryKeyRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, legacyArbitraryKey.StatusCode);

        using var otherEdgeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body,
            CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"));
        using var otherEdge = await client.SendAsync(otherEdgeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, otherEdge.StatusCode);
        Assert.NotEqual(
            result.ObjectKey,
            (await CoreApiTestClient.ReadAsync<PresignedUrlResult>(otherEdge)).ObjectKey);

        using var invalidIdentityRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body with { EventId = Guid.Empty },
            CoreApiTestClient.EdgeToken(edgeId, "C1"));
        using var invalidIdentity = await client.SendAsync(invalidIdentityRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalidIdentity.StatusCode);

        using var excessiveRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body with { LifetimeSeconds = 901 },
            CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1"));
        using var excessive = await client.SendAsync(excessiveRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, excessive.StatusCode);

        using var oversizedRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body with { ContentLength = 4_000_001 },
            CoreApiTestClient.EdgeToken(edgeId, "C1"));
        using var oversized = await client.SendAsync(oversizedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        using var boundaryRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body with
            {
                AttachmentType = new string('a', 64),
                ContentType = string.Concat("application/", new string('a', 116))
            },
            CoreApiTestClient.EdgeToken(edgeId, "C1"));
        using var boundary = await client.SendAsync(boundaryRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);

        using var excessiveAttachmentTypeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body with { AttachmentType = new string('a', 65) },
            CoreApiTestClient.EdgeToken(edgeId, "C1"));
        using var excessiveAttachmentType = await client.SendAsync(excessiveAttachmentTypeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, excessiveAttachmentType.StatusCode);

        using var excessiveContentTypeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body with { ContentType = string.Concat("application/", new string('a', 117)) },
            CoreApiTestClient.EdgeToken(edgeId, "C1"));
        using var excessiveContentType = await client.SendAsync(excessiveContentTypeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, excessiveContentType.StatusCode);

        foreach (var unsafeContentType in new[]
                 {
                     "image/jpeg; charset=utf-8",
                     "image/jpeg\r\nx-amz-meta-test: injected"
                 })
        {
            using var unsafeRequest = CoreApiTestClient.JsonRequest(
                HttpMethod.Post,
                "/api/edge/images/presigned-url",
                body with { ContentType = unsafeContentType },
                CoreApiTestClient.EdgeToken(edgeId, "C1"));
            using var unsafeResponse = await client.SendAsync(unsafeRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, unsafeResponse.StatusCode);
        }

        using var unsafeAttachmentRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/presigned-url",
            body with { AttachmentType = "parcel/image" },
            CoreApiTestClient.EdgeToken(edgeId, "C1"));
        using var unsafeAttachment = await client.SendAsync(unsafeAttachmentRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unsafeAttachment.StatusCode);
    }
}

public sealed class CoreEdgeHealthHostTests
{
    [Fact]
    public async Task EdgeWritesBindJwtEdgeIdAndOperationalEventsAreIdempotent()
    {
        using var factory = new CoreApiFactory<EdgeHealthApiMarker>();
        using var client = factory.CreateClient();
        var edgeId = Guid.NewGuid();
        var otherEdgeId = Guid.NewGuid();
        var token = CoreApiTestClient.EdgeToken(edgeId, "C1");
        var now = DateTimeOffset.UtcNow;
        var registration = new RegisterEdgeRequest(edgeId, "C1", "1.0.0", 1, now);

        using var health = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var anonymousRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/registration", registration);
        using var anonymous = await client.SendAsync(anonymousRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var crossEdgeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/registration",
            registration with { EdgeId = otherEdgeId },
            token);
        using var crossEdge = await client.SendAsync(crossEdgeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossEdge.StatusCode);

        using var crossCenterRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/registration",
            registration with { ExchangeCenterCode = "C2" },
            token);
        using var crossCenter = await client.SendAsync(crossCenterRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossCenter.StatusCode);

        using var multiCenterRegistrationRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/registration",
            registration,
            CoreApiTestClient.EdgeToken(edgeId, "C1", "C2"));
        using var multiCenterRegistration = await client.SendAsync(multiCenterRegistrationRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, multiCenterRegistration.StatusCode);

        using var negativeVersionRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/registration",
            registration with { ConfigurationVersion = -1 },
            token);
        using var negativeVersion = await client.SendAsync(negativeVersionRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, negativeVersion.StatusCode);

        using (var registerRequest = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post, "/api/edge/registration", registration, token))
        using (var registered = await client.SendAsync(registerRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, registered.StatusCode);
        }

        using (var replayRequest = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post, "/api/edge/registration", registration, token))
        using (var replay = await client.SendAsync(replayRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        }

        var heartbeat = new EdgeHeartbeatRequest(
            edgeId, now.AddMinutes(1), "1.0.1", 2, 10_000, 1, 2, 3, now);
        using var heartbeatRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/heartbeat", heartbeat, token);
        using var heartbeatResponse = await client.SendAsync(heartbeatRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, heartbeatResponse.StatusCode);

        using var multiCenterHeartbeatRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/heartbeat",
            heartbeat,
            CoreApiTestClient.EdgeToken(edgeId, "C1", "C2"));
        using var multiCenterHeartbeat = await client.SendAsync(multiCenterHeartbeatRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, multiCenterHeartbeat.StatusCode);

        using var negativeDiskRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/heartbeat",
            heartbeat with { HeartbeatAt = now.AddMinutes(2), AvailableDiskBytes = -1 },
            token);
        using var negativeDisk = await client.SendAsync(negativeDiskRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, negativeDisk.StatusCode);

        using var wrongHeartbeatRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/heartbeat",
            heartbeat with { EdgeId = otherEdgeId },
            token);
        using var wrongHeartbeat = await client.SendAsync(wrongHeartbeatRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrongHeartbeat.StatusCode);

        var sortingDeviceId = Guid.NewGuid();
        var operationalEvent = new OperationalEventRequest(
            OperationalEventType.DeviceIpBindingChanged,
            edgeId,
            sortingDeviceId,
            "event-one",
            null,
            now.AddMinutes(2));
        using (var eventRequest = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post, "/api/edge/events", operationalEvent, token, "event-one"))
        using (var eventResponse = await client.SendAsync(eventRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, eventResponse.StatusCode);
        }

        using var multiCenterEventRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/events",
            operationalEvent with { IdempotencyKey = "event-multi" },
            CoreApiTestClient.EdgeToken(edgeId, "C1", "C2"),
            "event-multi");
        using var multiCenterEvent = await client.SendAsync(multiCenterEventRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, multiCenterEvent.StatusCode);

        using (var eventReplayRequest = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post, "/api/edge/events", operationalEvent, token, "event-one", "retry-corr"))
        using (var eventReplay = await client.SendAsync(eventReplayRequest, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, eventReplay.StatusCode);
        }

        using var eventConflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/events",
            operationalEvent with { EventType = OperationalEventType.AuthenticationRejected },
            token,
            "event-one");
        using var eventConflict = await client.SendAsync(eventConflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, eventConflict.StatusCode);

        using var sameCenterOtherEdgeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/events",
            operationalEvent with { EdgeId = otherEdgeId, IdempotencyKey = "other-edge" },
            token,
            "other-edge");
        using var sameCenterOtherEdge = await client.SendAsync(sameCenterOtherEdgeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, sameCenterOtherEdge.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<EdgeHealthDbContext>();
        Assert.Equal(1, await database.EdgeHealthStatuses.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.OperationalEvents.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.IdempotencyRecords.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(10_000, (await database.EdgeHealthStatuses.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).AvailableDiskBytes);
        Assert.Equal(sortingDeviceId, (await database.OperationalEvents.SingleAsync(cancellationToken: TestContext.Current.CancellationToken)).DeviceId?.Value);
    }

    [Fact]
    public async Task AdministratorHealthQueryFiltersByAllowedCenter()
    {
        using var factory = new CoreApiFactory<EdgeHealthApiMarker>();
        using var client = factory.CreateClient();
        var edgeId = Guid.NewGuid();
        var registration = new RegisterEdgeRequest(edgeId, "C1", "1.0", 1, DateTimeOffset.UtcNow);
        using (var request = CoreApiTestClient.JsonRequest(
                   HttpMethod.Post, "/api/edge/registration", registration, CoreApiTestClient.EdgeToken(edgeId, "C1")))
        using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using var centerOneRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/admin/edges/health",
            token: CoreApiTestClient.AdministratorToken("CenterAdministrator", "C1"));
        using var centerOne = await client.SendAsync(centerOneRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, centerOne.StatusCode);
        Assert.Equal(1, (await CoreApiTestClient.ReadAsync<Post.Core.EdgeHealth.Api.PagedResponse<EdgeHealthDto>>(centerOne)).TotalCount);

        using var isolatedRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/admin/edges/health",
            token: CoreApiTestClient.AdministratorToken("CenterAdministrator", "C2"));
        using var isolated = await client.SendAsync(isolatedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await CoreApiTestClient.ReadAsync<Post.Core.EdgeHealth.Api.PagedResponse<EdgeHealthDto>>(isolated)).TotalCount);

        using var wrongRoleRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Get,
            "/api/admin/edges/health",
            token: CoreApiTestClient.EdgeToken(edgeId, "C1"));
        using var wrongRole = await client.SendAsync(wrongRoleRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);
    }
}

public sealed class CoreOperationalResultHostTests
{
    [Fact]
    public async Task OperationalResultRequiresEdgeAndPersistsOneResultPerIdempotencyKey()
    {
        using var factory = new CoreApiFactory<OperationalResultApiMarker>();
        using var client = factory.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var edgeId = Guid.NewGuid();
        var body = new OperationalResultRequest(
            edgeId,
            "C1",
            "operation-one",
            null,
            "123456789012345678901234",
            "accepted",
            true,
            null,
            null,
            now,
            now.AddMilliseconds(25),
            1,
            OperationalFinalStatus.Succeeded);
        var edge = CoreApiTestClient.EdgeToken(edgeId, "C1");

        using var health = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var anonymousRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/operational-results", body, idempotencyKey: "operation-one");
        using var anonymous = await client.SendAsync(anonymousRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var wrongRoleRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/operational-results",
            body,
            CoreApiTestClient.AdministratorToken(),
            "operation-one");
        using var wrongRole = await client.SendAsync(wrongRoleRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrongRole.StatusCode);

        using var crossEdgeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/operational-results",
            body with { EdgeId = Guid.NewGuid() },
            edge,
            "operation-one");
        using var crossEdge = await client.SendAsync(crossEdgeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossEdge.StatusCode);

        using var crossCenterRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/operational-results",
            body with { ExchangeCenterCode = "C2" },
            edge,
            "operation-one");
        using var crossCenter = await client.SendAsync(crossCenterRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossCenter.StatusCode);

        using var missingKeyRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/operational-results", body with { IdempotencyKey = null }, edge);
        using var missingKey = await client.SendAsync(missingKeyRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);

        using var firstRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/operational-results", body, edge, "operation-one", "wire-one");
        using var first = await client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Null(first.Headers.Location);
        var stored = await CoreApiTestClient.ReadAsync<StorageResult>(first);
        Assert.False(stored.IsReplay);

        using var replayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/operational-results", body, edge, "operation-one", "wire-retry");
        using var replay = await client.SendAsync(replayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True((await CoreApiTestClient.ReadAsync<StorageResult>(replay)).IsReplay);

        using var conflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/operational-results",
            body with { CallResult = "different" },
            edge,
            "operation-one");
        using var conflict = await client.SendAsync(conflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var invalidRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/operational-results",
            body with
            {
                IdempotencyKey = "invalid-one",
                RespondedAt = now.AddSeconds(-1)
            },
            edge,
            "invalid-one");
        using var invalid = await client.SendAsync(invalidRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OperationalResultDbContext>();
        var persisted = Assert.Single(await database.OperationalResults.ToArrayAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(edgeId, persisted.EdgeId.Value);
        Assert.Equal("C1", persisted.ExchangeCenterCode.Value);
        Assert.Equal(1, await database.IdempotencyRecords.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }
}
