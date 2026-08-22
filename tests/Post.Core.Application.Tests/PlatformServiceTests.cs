using Microsoft.EntityFrameworkCore;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Application.Operations;
using Post.Core.Domain.Common;
using Post.Core.Infrastructure.Persistence;
using Post.Core.Infrastructure.Testing;

namespace Post.Core.Application.Tests;

public sealed class PlatformServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PresignedUrlServiceDerivesIdentityBoundKeyAndValidatesInputs()
    {
        var presigner = new RecordingPresigner();
        var service = new PresignedUrlService(presigner, new MutableTimeProvider(Now));
        var edgeId = Guid.NewGuid();
        var readingId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var command = new PresignedUrlCommand(
            eventId,
            edgeId,
            "C1",
            readingId,
            "parcel-image",
            "image/jpeg",
            1234,
            TimeSpan.FromMinutes(5));

        var result = service.Create(command);
        var expectedKey = EdgeUploadObjectKey.Create(edgeId, "C1", readingId, eventId);
        Assert.Equal(expectedKey, presigner.ObjectKey);
        Assert.Equal(expectedKey, result.ObjectKey);
        Assert.Equal(1234, presigner.ContentLength);
        Assert.Equal("image/jpeg", presigner.ContentType);
        Assert.Equal(Now.AddMinutes(5), result.ExpiresAt);
        Assert.NotEqual(
            expectedKey,
            EdgeUploadObjectKey.Create(Guid.NewGuid(), "C1", readingId, eventId));
        Assert.NotEqual(
            expectedKey,
            EdgeUploadObjectKey.Create(edgeId, "C2", readingId, eventId));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { EventId = Guid.Empty }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { ContentType = string.Empty }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { AttachmentType = string.Empty }));
        Assert.NotNull(service.Create(command with
        {
            AttachmentType = new string('a', 64),
            ContentType = string.Concat("application/", new string('a', 116))
        }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { AttachmentType = new string('a', 65) }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { ContentType = string.Concat("application/", new string('a', 117)) }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { ContentType = "image/jpeg\r\nx-amz-meta-test: injected" }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { ContentType = "image/jpeg; charset=utf-8" }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { AttachmentType = "parcel/image" }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { AttachmentType = "parcel image" }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { ContentLength = 0 }));
        Assert.Throws<CoreValidationException>(() => service.Create(
            command with { Lifetime = TimeSpan.Zero }));
    }

    [Fact]
    public async Task EdgeHealthLifecyclePersistsHeartbeatEventAndOfflineTransition()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var store = new EfEdgeHealthStore(database.EdgeHealth);
        var time = new MutableTimeProvider(Now);
        var service = new EdgeHealthService(
            store,
            new CanonicalJsonRequestHasher(),
            new IdempotencyService(),
            time);
        var edgeId = Guid.NewGuid();

        await service.RegisterAsync(new RegisterEdgeCommand(edgeId, "C1", "1.0", 1, Now), cancellationToken: TestContext.Current.CancellationToken);
        await service.RecordHeartbeatAsync(new EdgeHeartbeatCommand(
            edgeId, Now.AddMinutes(1), "1.1", 2, 10_000, 3, 2, 1, Now), cancellationToken: TestContext.Current.CancellationToken);
        await service.RecordEventAsync(new RecordOperationalEventCommand(
            OperationalEventType.Connected, edgeId, null, "event-one", "corr", Now), cancellationToken: TestContext.Current.CancellationToken);
        await service.RecordEventAsync(new RecordOperationalEventCommand(
            OperationalEventType.Connected, edgeId, null, "event-one", "retry-corr", Now), cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<CoreConflictException>(() => service.RecordEventAsync(
            new RecordOperationalEventCommand(
                OperationalEventType.Disconnected, edgeId, null, "event-one", "corr", Now), cancellationToken: TestContext.Current.CancellationToken));
        var status = Assert.Single(await service.ListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, status.PendingCount);
        Assert.Equal(10_000, status.AvailableDiskBytes);
        Assert.Equal("1.1", status.SoftwareVersion);
        Assert.Equal(1, await database.EdgeHealth.OperationalEvents.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.EdgeHealth.IdempotencyRecords.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await database.EdgeHealth.OutboxMessages.CountAsync(TestContext.Current.CancellationToken));

        time.Advance(TimeSpan.FromMinutes(10));
        var monitor = new EdgeHealthMonitor(store, time);
        Assert.Equal(1, await monitor.MarkStaleEdgesOfflineAsync(TimeSpan.FromMinutes(5), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(EdgeConnectionState.Offline, (await service.ListAsync(TestContext.Current.CancellationToken)).Single().ConnectionState);
        await Assert.ThrowsAsync<CoreConflictException>(() => service.RegisterAsync(
            new RegisterEdgeCommand(edgeId, "OTHER", "1.0", 1, Now.AddMinutes(2)), cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreNotFoundException>(() => service.RecordHeartbeatAsync(new EdgeHeartbeatCommand(
            Guid.NewGuid(), Now, "1.0", 1, 0, 0, 0, 0, null), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OperationalResultIsIdempotentAndConflictsOnDifferentPayload()
    {
        await using var database = await SqliteCoreTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var service = new OperationalResultService(
            new EfOperationalResultStore(database.OperationalResult),
            new CanonicalJsonRequestHasher(),
            new MutableTimeProvider(Now));
        var command = new OperationalResultCommand(
            Guid.NewGuid(), "C1", "key", "corr", null, "ok", true, null, null, Now, Now.AddSeconds(1), 1,
            OperationalFinalStatus.Succeeded);

        var first = await service.StoreAsync(command, cancellationToken: TestContext.Current.CancellationToken);
        var replay = await service.StoreAsync(command with { CorrelationId = "retry-correlation" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.Identifier, replay.Identifier);
        await Assert.ThrowsAsync<CoreConflictException>(() => service.StoreAsync(
            command with { CallResult = "different" }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreConflictException>(() => service.StoreAsync(
            command with { EdgeId = Guid.NewGuid() }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<CoreConflictException>(() => service.StoreAsync(
            command with { ExchangeCenterCode = "C2" }, cancellationToken: TestContext.Current.CancellationToken));
    }
}
