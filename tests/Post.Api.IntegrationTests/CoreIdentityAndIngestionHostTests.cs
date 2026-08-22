using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.DataIngestion.Api;
using Post.Core.Domain.Common;
using Post.Core.Domain.ValueObjects;
using Post.Core.Identity.Api;
using Post.Core.Infrastructure.Messaging;
using Post.Core.Infrastructure.Persistence;

namespace Post.Api.IntegrationTests;

public sealed class CoreIdentityHostTests
{
    private const string AdminUserName = "bootstrap-admin";
    private const string SyntheticAdminPassphrase = "correct horse battery staple";

    [Fact]
    public async Task AnonymousAdminTokenWorksAndInvalidCredentialsRemainSafe()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var health = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var validRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/token",
            new AdminTokenRequest(AdminUserName, SyntheticAdminPassphrase, 600));
        using var valid = await client.SendAsync(validRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        var token = await CoreApiTestClient.ReadAsync<AccessTokenResult>(valid);
        Assert.False(string.IsNullOrWhiteSpace(token.Token));

        using var nullPasswordRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/token",
            new { userName = AdminUserName, password = (string?)null, tokenLifetimeSeconds = 600 });
        using var nullPassword = await client.SendAsync(nullPasswordRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, nullPassword.StatusCode);

        using var invalidRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/token",
            new AdminTokenRequest(AdminUserName, "wrong-password", 600));
        using var invalid = await client.SendAsync(invalidRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.DoesNotContain("stack", await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnonymousTokenEndpointsRejectOversizedCredentialInputs()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var requests = new[]
        {
            CoreApiTestClient.JsonRequest(
                HttpMethod.Post,
                "/api/auth/token",
                new AdminTokenRequest(
                    new string('u', AuthenticationInputLimits.UserNameMaximumLength + 1),
                    SyntheticAdminPassphrase,
                    600)),
            CoreApiTestClient.JsonRequest(
                HttpMethod.Post,
                "/api/auth/token",
                new AdminTokenRequest(
                    AdminUserName,
                    new string('p', AuthenticationInputLimits.PasswordMaximumLength + 1),
                    600)),
            CoreApiTestClient.JsonRequest(
                HttpMethod.Post,
                "/api/auth/edge-token",
                new EdgeTokenRequest(
                    new string('d', AuthenticationInputLimits.LogicalDeviceCodeMaximumLength + 1),
                    "candidate-secret",
                    300)),
            CoreApiTestClient.JsonRequest(
                HttpMethod.Post,
                "/api/auth/edge-token",
                new EdgeTokenRequest(
                    "EDGE-1",
                    new string('s', AuthenticationInputLimits.DeviceSecretMaximumLength + 1),
                    300))
        };

        foreach (var request in requests)
        {
            using (request)
            using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
        }

        var oversizedBodyRequests = new[]
        {
            CoreApiTestClient.JsonRequest(
                HttpMethod.Post,
                "/api/auth/token",
                new AdminTokenRequest(
                    AdminUserName,
                    new string('p', checked((int)AuthenticationInputLimits.TokenRequestBodyMaximumBytes)),
                    600)),
            CoreApiTestClient.JsonRequest(
                HttpMethod.Post,
                "/api/auth/edge-token",
                new EdgeTokenRequest(
                    "EDGE-1",
                    new string('s', checked((int)AuthenticationInputLimits.TokenRequestBodyMaximumBytes)),
                    300))
        };
        foreach (var request in oversizedBodyRequests)
        {
            using (request)
            using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
            {
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            }
        }

        var chunkedPayload = Encoding.UTF8.GetBytes(string.Concat(
            "{\"userName\":\"",
            AdminUserName,
            "\",\"password\":\"",
            new string('p', checked((int)AuthenticationInputLimits.TokenRequestBodyMaximumBytes)),
            "\",\"tokenLifetimeSeconds\":600}"));
        using var chunkedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/api/auth/token", UriKind.Relative))
        {
            Content = new StreamContent(new MemoryStream(chunkedPayload, writable: false))
        };
        chunkedRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        chunkedRequest.Content.Headers.ContentLength = null;
        chunkedRequest.Headers.TransferEncodingChunked = true;
        using var chunkedResponse = await client.SendAsync(chunkedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
    }

    [Fact]
    public async Task AnonymousTokenEndpointsShareGlobalSecondaryLimitAndIgnoreRawForwardingHeaders()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Identity:AuthenticationRateLimit:PermitLimit"] = "1",
            ["Identity:AuthenticationRateLimit:WindowSeconds"] = "3600",
            ["Identity:AuthenticationRateLimit:QueueLimit"] = "0"
        });
        using var client = factory.CreateClient();

        using var firstRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/token",
            new AdminTokenRequest("missing-user", "synthetic-invalid-value", 600));
        firstRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.10");
        using var first = await client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);

        using var secondRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/edge-token",
            new EdgeTokenRequest("MISSING-EDGE", "synthetic-invalid-value", 300));
        secondRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        using var second = await client.SendAsync(secondRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.True(second.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task DefaultSecondaryLimitAllowsElevenSimultaneousGatewayEdgeRefreshes()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var requests = Enumerable.Range(1, 11)
            .Select(index =>
            {
                var request = CoreApiTestClient.JsonRequest(
                    HttpMethod.Post,
                    "/api/auth/edge-token",
                    new EdgeTokenRequest($"MISSING-{index}", "synthetic-invalid-value", 300));
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"203.0.113.{index}");
                return request;
            })
            .ToArray();

        try
        {
            var responses = await Task.WhenAll(requests.Select(client.SendAsync));
            try
            {
                Assert.All(responses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            foreach (var request in requests)
            {
                request.Dispose();
            }
        }
    }

    [Fact]
    public async Task AdministratorProvisionsEdgeSecretOnceThenCanDisableCredential()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var deviceId = Guid.NewGuid();
        var provisionBody = new ProvisionEdgeRequest(deviceId, "EDGE-ONE", "C1");

        using var unauthorizedRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/edges", provisionBody, idempotencyKey: "provision-unauthorized");
        using var unauthorized = await client.SendAsync(unauthorizedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var forbiddenRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/admin/edges",
            provisionBody,
            CoreApiTestClient.EdgeToken(deviceId, "C1"),
            "provision-forbidden");
        using var forbidden = await client.SendAsync(forbiddenRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var adminToken = await AuthenticateAdminAsync(client);
        using var provisionRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/edges", provisionBody, adminToken, "provision-one");
        using var provisioned = await client.SendAsync(provisionRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, provisioned.StatusCode);
        Assert.Null(provisioned.Headers.Location);
        var credential = await CoreApiTestClient.ReadAsync<ProvisionedEdgeResult>(provisioned);
        Assert.Equal(deviceId, credential.DeviceId);
        var rawSecret = Assert.IsType<string>(credential.DeviceSecret);
        Assert.False(string.IsNullOrWhiteSpace(rawSecret));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIdentityStore>();
            var persisted = await store.FindEdgeCredentialAsync(new DeviceId(deviceId), TestContext.Current.CancellationToken);
            Assert.NotNull(persisted);
            Assert.NotEqual(rawSecret, persisted.SecretVerifier.Hash);
            Assert.True(scope.ServiceProvider.GetRequiredService<ISecretHasher>()
                .Verify(rawSecret, persisted.SecretVerifier));
            var database = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            Assert.Contains(database.AuditRecords, x => x.Action == "edge.provision");
            Assert.DoesNotContain(database.IdempotencyRecords, x => x.ResponseSnapshot.Contains(rawSecret, StringComparison.Ordinal));
            Assert.DoesNotContain(database.OutboxMessages, x => x.Payload.Contains(rawSecret, StringComparison.Ordinal));
            Assert.DoesNotContain(database.AuditRecords, x => x.Details.Contains(rawSecret, StringComparison.Ordinal));
        }

        using var replayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/edges", provisionBody, adminToken, "provision-one");
        using var replay = await client.SendAsync(replayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await CoreApiTestClient.ReadAsync<ProvisionedEdgeResult>(replay);
        Assert.True(replayed.IsReplay);
        Assert.True(replayed.RecoveryRequired);
        Assert.Null(replayed.DeviceSecret);

        using var conflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/admin/edges",
            provisionBody with { LogicalDeviceCode = "EDGE-DIFFERENT" },
            adminToken,
            "provision-one");
        using var conflict = await client.SendAsync(conflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var duplicateRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/admin/edges", provisionBody, adminToken, "provision-two");
        using var duplicate = await client.SendAsync(duplicateRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.DoesNotContain(rawSecret, await duplicate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        using var edgeTokenRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/edge-token",
            new EdgeTokenRequest("EDGE-ONE", rawSecret, 300));
        using var edgeTokenResponse = await client.SendAsync(edgeTokenRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, edgeTokenResponse.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(
            (await CoreApiTestClient.ReadAsync<AccessTokenResult>(edgeTokenResponse)).Token));

        using var rotateRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            $"/api/admin/edges/{deviceId:D}/rotate-secret",
            token: adminToken,
            idempotencyKey: "rotate-one");
        using var rotatedResponse = await client.SendAsync(rotateRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, rotatedResponse.StatusCode);
        var rotated = await CoreApiTestClient.ReadAsync<ProvisionedEdgeResult>(rotatedResponse);
        var replacementSecret = Assert.IsType<string>(rotated.DeviceSecret);
        Assert.NotEqual(rawSecret, replacementSecret);

        using var rotateReplayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            $"/api/admin/edges/{deviceId:D}/rotate-secret",
            token: adminToken,
            idempotencyKey: "rotate-one");
        using var rotateReplayResponse = await client.SendAsync(rotateReplayRequest, TestContext.Current.CancellationToken);
        var rotateReplay = await CoreApiTestClient.ReadAsync<ProvisionedEdgeResult>(rotateReplayResponse);
        Assert.True(rotateReplay.IsReplay);
        Assert.True(rotateReplay.RecoveryRequired);
        Assert.Null(rotateReplay.DeviceSecret);

        using var oldSecretRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/edge-token",
            new EdgeTokenRequest("EDGE-ONE", rawSecret, 300));
        using var oldSecretResponse = await client.SendAsync(oldSecretRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSecretResponse.StatusCode);

        using var replacementRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/edge-token",
            new EdgeTokenRequest("EDGE-ONE", replacementSecret, 300));
        using var replacementResponse = await client.SendAsync(replacementRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, replacementResponse.StatusCode);

        using var crossCenterDisableRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            $"/api/admin/edges/{deviceId:D}/disable",
            token: CoreApiTestClient.AdministratorToken("CenterAdministrator", "C2"),
            idempotencyKey: "disable-cross-center");
        using var crossCenterDisable = await client.SendAsync(crossCenterDisableRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossCenterDisable.StatusCode);

        using var disableRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            $"/api/admin/edges/{deviceId:D}/disable",
            token: adminToken,
            idempotencyKey: "disable-one");
        using var disabled = await client.SendAsync(disableRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);

        using var disableReplayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            $"/api/admin/edges/{deviceId:D}/disable",
            token: adminToken,
            idempotencyKey: "disable-one");
        using var disableReplay = await client.SendAsync(disableReplayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, disableReplay.StatusCode);

        using var disabledTokenRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/edge-token",
            new EdgeTokenRequest("EDGE-ONE", replacementSecret, 300));
        using var disabledToken = await client.SendAsync(disabledTokenRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, disabledToken.StatusCode);
        Assert.DoesNotContain(factory.LogMessages,
            message => message.Contains(rawSecret, StringComparison.Ordinal) ||
                       message.Contains(replacementSecret, StringComparison.Ordinal));
    }

    private static CoreApiFactory<IdentityApiMarker> CreateFactory(
        IReadOnlyDictionary<string, string?>? additionalSettings = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Identity:InitialAdmin:Enabled"] = "true",
            ["Identity:InitialAdmin:UserName"] = AdminUserName,
            ["Identity:InitialAdmin:Password"] = SyntheticAdminPassphrase,
            ["Identity:InitialAdmin:Roles:0"] = nameof(AdminRole.SystemAdministrator)
        };
        if (additionalSettings is not null)
        {
            foreach (var setting in additionalSettings)
            {
                settings[setting.Key] = setting.Value;
            }
        }

        return new CoreApiFactory<IdentityApiMarker>(settings);
    }

    private static async Task<string> AuthenticateAdminAsync(HttpClient client)
    {
        using var request = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/auth/token",
            new AdminTokenRequest(AdminUserName, SyntheticAdminPassphrase, 600));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await CoreApiTestClient.ReadAsync<AccessTokenResult>(response)).Token;
    }
}

public sealed class CoreDataIngestionHostTests
{
    [Fact]
    public async Task AllIngestionRoutesRejectOversizedBodiesBeforeModelBindingIncludingChunked()
    {
        using var factory = new CoreApiFactory<DataIngestionApiMarker>();
        using var client = factory.CreateClient();
        var token = CoreApiTestClient.EdgeToken(Guid.NewGuid(), "C1");
        var oversized = Encoding.UTF8.GetBytes(string.Concat(
            "{\"padding\":\"",
            new string('x', IngestionPayloadLimits.MaximumRequestBodyBytes),
            "\"}"));

        foreach (var path in new[]
                 {
                     "/api/edge/parcel-events",
                     "/api/edge/images/metadata",
                     "/api/edge/errors"
                 })
        {
            using var request = RawIngestionRequest(path, token, oversized, chunked: false);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }

        using var chunkedRequest = RawIngestionRequest("/api/edge/errors", token, oversized, chunked: true);
        using var chunkedResponse = await client.SendAsync(chunkedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedResponse.StatusCode);
        Assert.Empty(factory.Services.GetRequiredService<InMemoryDurableEventPublisher>().Events);
    }

    [Fact]
    public async Task ParcelEventRequiresEdgeAndDurablyDeduplicatesOrConflicts()
    {
        using var factory = new CoreApiFactory<DataIngestionApiMarker>();
        using var client = factory.CreateClient();
        var deviceId = Guid.NewGuid();
        var edgeToken = CoreApiTestClient.EdgeToken(deviceId, "C1");
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;
        var body = new ParcelEventRequest(
            eventId,
            deviceId,
            "C1",
            "parcel.events.received",
            "parcel-event-key",
            "body-correlation",
            new ParcelFinalPayload("123456789012345678901234", CoreParcelStatus.Success),
            occurredAt);

        using var health = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var anonymousRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/parcel-events", body, idempotencyKey: "parcel-event-key");
        using var anonymous = await client.SendAsync(anonymousRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var adminRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/parcel-events",
            body,
            CoreApiTestClient.AdministratorToken(),
            "parcel-event-key");
        using var admin = await client.SendAsync(adminRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);

        using var crossEdgeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/parcel-events",
            body with { EdgeId = Guid.NewGuid() },
            edgeToken,
            "parcel-event-key");
        using var crossEdge = await client.SendAsync(crossEdgeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossEdge.StatusCode);

        using var crossCenterRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/parcel-events",
            body with { ExchangeCenterCode = "C2" },
            edgeToken,
            "parcel-event-key");
        using var crossCenter = await client.SendAsync(crossCenterRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossCenter.StatusCode);

        using var missingKeyRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/parcel-events", body, edgeToken);
        using var missingKey = await client.SendAsync(missingKeyRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);

        using var firstRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/parcel-events", body, edgeToken, "parcel-event-key", "fixed-correlation");
        using var first = await client.SendAsync(firstRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        using var replayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/parcel-events", body, edgeToken, "parcel-event-key", "fixed-correlation");
        using var replay = await client.SendAsync(replayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);

        using var conflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/parcel-events",
            body with { Payload = body.Payload with { FinalStatus = CoreParcelStatus.Error } },
            edgeToken,
            "parcel-event-key",
            "fixed-correlation");
        using var conflict = await client.SendAsync(conflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var publisher = factory.Services.GetRequiredService<InMemoryDurableEventPublisher>();
        var persisted = Assert.Single(publisher.Events);
        Assert.Equal("parcel.events.received", persisted.Topic);
        Assert.Contains("ParcelBarcode", persisted.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachmentMetadataEnforcesPersistableFieldBoundsBeforePublishing()
    {
        using var factory = new CoreApiFactory<DataIngestionApiMarker>();
        using var client = factory.CreateClient();
        var edgeId = Guid.NewGuid();
        var edgeToken = CoreApiTestClient.EdgeToken(edgeId, "C1");
        var readingRecordId = Guid.NewGuid();

        ImageMetadataRequest Metadata(string contentType, string attachmentType)
        {
            var eventId = Guid.NewGuid();
            return new ImageMetadataRequest(
                eventId,
                edgeId,
                "C1",
                "123456789012345678901234",
                readingRecordId,
                EdgeUploadObjectKey.Create(edgeId, "C1", readingRecordId, eventId),
                contentType,
                attachmentType,
                null,
                null,
                DateTimeOffset.UtcNow);
        }

        using var boundaryRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/metadata",
            Metadata(string.Concat("application/", new string('a', 116)), new string('a', 64)),
            edgeToken,
            "metadata-boundary");
        using var boundary = await client.SendAsync(boundaryRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, boundary.StatusCode);

        using var excessiveContentTypeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/metadata",
            Metadata(string.Concat("application/", new string('a', 117)), "parcel-image"),
            edgeToken,
            "metadata-content-too-long");
        using var excessiveContentType = await client.SendAsync(excessiveContentTypeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, excessiveContentType.StatusCode);

        using var excessiveAttachmentTypeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/metadata",
            Metadata("image/jpeg", new string('a', 65)),
            edgeToken,
            "metadata-attachment-too-long");
        using var excessiveAttachmentType = await client.SendAsync(excessiveAttachmentTypeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, excessiveAttachmentType.StatusCode);
        using var unsafeContentTypeRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/metadata",
            Metadata("image/jpeg\r\nx-test: injected", "parcel-image"),
            edgeToken,
            "metadata-unsafe-content");
        using var unsafeContentType = await client.SendAsync(unsafeContentTypeRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, unsafeContentType.StatusCode);
        Assert.Single(factory.Services.GetRequiredService<InMemoryDurableEventPublisher>().Events);
    }

    [Fact]
    public async Task AttachmentMetadataAndErrorRoutesValidateThenPublishTypedEvents()
    {
        using var factory = new CoreApiFactory<DataIngestionApiMarker>();
        using var client = factory.CreateClient();
        var edgeId = Guid.NewGuid();
        var edgeToken = CoreApiTestClient.EdgeToken(edgeId, "C1");
        var now = DateTimeOffset.UtcNow;
        var metadataEventId = Guid.NewGuid();
        var readingRecordId = Guid.NewGuid();
        var metadata = new ImageMetadataRequest(
            metadataEventId,
            edgeId,
            "C1",
            "123456789012345678901234",
            readingRecordId,
            EdgeUploadObjectKey.Create(edgeId, "C1", readingRecordId, metadataEventId),
            "application/json",
            "supplementary-data",
            null,
            null,
            now);

        using var metadataRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/images/metadata", metadata, edgeToken, "metadata-key");
        using var metadataResponse = await client.SendAsync(metadataRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, metadataResponse.StatusCode);

        using var crossEdgeMetadataRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/metadata",
            metadata with { EdgeId = Guid.NewGuid() },
            edgeToken,
            "cross-edge-metadata");
        using var crossEdgeMetadata = await client.SendAsync(crossEdgeMetadataRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossEdgeMetadata.StatusCode);

        using var multiCenterMetadataRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/metadata",
            metadata,
            CoreApiTestClient.EdgeToken(edgeId, "C1", "C2"),
            "multi-center-metadata");
        using var multiCenterMetadata = await client.SendAsync(multiCenterMetadataRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, multiCenterMetadata.StatusCode);

        using var metadataReplayRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/images/metadata", metadata, edgeToken, "metadata-key", "retry-corr");
        using var metadataReplay = await client.SendAsync(metadataReplayRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, metadataReplay.StatusCode);

        using var metadataConflictRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/metadata",
            metadata with { ContentType = "application/problem+json" },
            edgeToken,
            "metadata-key");
        using var metadataConflict = await client.SendAsync(metadataConflictRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, metadataConflict.StatusCode);

        using var invalidRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/images/metadata",
            metadata with { EventId = Guid.NewGuid(), ObjectKey = "../secret" },
            edgeToken,
            "invalid-metadata");
        using var invalid = await client.SendAsync(invalidRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var document = System.Text.Json.JsonDocument.Parse("{\"code\":\"timeout\"}");
        var error = new EdgeErrorRequest(
            Guid.NewGuid(),
            edgeId,
            "C1",
            "edge.integration-error.v1",
            null,
            null,
            document.RootElement.Clone(),
            now);
        using var crossEdgeErrorRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/errors",
            error with { EdgeId = Guid.NewGuid() },
            edgeToken,
            "cross-edge-error");
        using var crossEdgeError = await client.SendAsync(crossEdgeErrorRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossEdgeError.StatusCode);

        using var errorRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post, "/api/edge/errors", error, edgeToken, "error-key");
        using var errorResponse = await client.SendAsync(errorRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, errorResponse.StatusCode);

        using var oversizedDocument = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                blob = new string('x', IngestionPayloadLimits.MaximumUtf8Bytes)
            }));
        var oversizedError = error with
        {
            EventId = Guid.NewGuid(),
            Payload = oversizedDocument.RootElement.Clone()
        };
        using var oversizedErrorRequest = CoreApiTestClient.JsonRequest(
            HttpMethod.Post,
            "/api/edge/errors",
            oversizedError,
            edgeToken,
            "oversized-error");
        using var oversizedErrorResponse = await client.SendAsync(oversizedErrorRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedErrorResponse.StatusCode);
        Assert.Equal(2, factory.Services.GetRequiredService<InMemoryDurableEventPublisher>().Events.Count);
    }

    private static HttpRequestMessage RawIngestionRequest(
        string path,
        string token,
        byte[] payload,
        bool chunked)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StreamContent(new MemoryStream(payload, writable: false))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", "oversized-prebinding");
        if (chunked)
        {
            request.Content.Headers.ContentLength = null;
            request.Headers.TransferEncodingChunked = true;
        }

        return request;
    }
}
