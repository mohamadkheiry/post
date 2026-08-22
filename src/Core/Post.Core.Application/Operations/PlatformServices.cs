using System.Text.Json;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Messaging;
using Post.Core.Domain.Operations;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Application.Operations;

public sealed class PresignedUrlService : IPresignedUrlService
{
    private readonly IS3Presigner _presigner;
    private readonly TimeProvider _timeProvider;

    public PresignedUrlService(IS3Presigner presigner, TimeProvider timeProvider)
    {
        _presigner = presigner;
        _timeProvider = timeProvider;
    }

    public PresignedUrlResult Create(PresignedUrlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Lifetime <= TimeSpan.Zero ||
            command.ContentLength <= 0)
        {
            throw new CoreValidationException(
                "Attachment type, positive content length, and positive URL lifetime are required.");
        }

        _ = AttachmentMetadataRules.NormalizeAttachmentType(command.AttachmentType);
        var contentType = AttachmentMetadataRules.NormalizeContentType(command.ContentType);

        var objectKey = EdgeUploadObjectKey.Create(
            command.EdgeId,
            command.ExchangeCenterCode,
            command.ReadingRecordId,
            command.EventId);
        return _presigner.CreateUploadUrl(
            objectKey,
            contentType,
            command.ContentLength,
            _timeProvider.GetUtcNow(),
            command.Lifetime);
    }
}

public sealed class EdgeHealthService : IEdgeHealthService
{
    private readonly IEdgeHealthStore _store;
    private readonly IRequestHasher _requestHasher;
    private readonly IIdempotencyService _idempotencyService;
    private readonly TimeProvider _timeProvider;

    public EdgeHealthService(
        IEdgeHealthStore store,
        IRequestHasher requestHasher,
        IIdempotencyService idempotencyService,
        TimeProvider timeProvider)
    {
        _store = store;
        _requestHasher = requestHasher;
        _idempotencyService = idempotencyService;
        _timeProvider = timeProvider;
    }

    public async Task RegisterAsync(RegisterEdgeCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var edgeId = new EdgeId(command.EdgeId);
        var existing = await _store.FindAsync(edgeId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            _store.AddStatus(EdgeHealthStatus.Register(
                edgeId,
                new ExchangeCenterCode(command.ExchangeCenterCode),
                command.RegisteredAt,
                command.SoftwareVersion,
                command.ConfigurationVersion));
        }
        else if (existing.ExchangeCenterCode != new ExchangeCenterCode(command.ExchangeCenterCode))
        {
            throw new CoreConflictException("An Edge identifier cannot be registered for another center.");
        }
        else
        {
            existing.ApplyHeartbeat(
                command.RegisteredAt,
                command.SoftwareVersion,
                command.ConfigurationVersion,
                existing.AvailableDiskBytes,
                existing.PendingCount,
                existing.FailedCount,
                existing.DeadLetterCount,
                existing.LastSuccessfulSync);
        }

        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordHeartbeatAsync(
        EdgeHeartbeatCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var status = await _store.FindAsync(new EdgeId(command.EdgeId), cancellationToken).ConfigureAwait(false)
            ?? throw new CoreNotFoundException("Edge registration does not exist.");
        status.ApplyHeartbeat(
            command.HeartbeatAt,
            command.SoftwareVersion,
            command.ConfigurationVersion,
            command.AvailableDiskBytes,
            command.PendingCount,
            command.FailedCount,
            command.DeadLetterCount,
            command.LastSuccessfulSync);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordEventAsync(
        RecordOperationalEventCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = new IdempotencyKey(command.IdempotencyKey);
        var requestHash = _requestHasher.Hash(new
        {
            Operation = "edge-health.record-event",
            command.EventType,
            command.EdgeId,
            command.DeviceId,
            command.OccurredAt
        });
        var existing = await _store.FindIdempotencyAsync(key, cancellationToken).ConfigureAwait(false);
        var decision = _idempotencyService.Evaluate(existing, requestHash);
        if (decision == IdempotencyDecision.Replay)
        {
            return;
        }

        if (decision == IdempotencyDecision.InProgress)
        {
            throw new CoreConflictException("Operational event with this idempotency key is still in progress.");
        }

        var edgeId = new EdgeId(command.EdgeId);
        if (await _store.FindAsync(edgeId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new CoreNotFoundException("Edge registration does not exist.");
        }

        var operationalEvent = OperationalEvent.Create(
            command.EventType,
            edgeId,
            command.DeviceId is null ? null : new DeviceId(command.DeviceId.Value),
            new CorrelationId(command.CorrelationId),
            command.OccurredAt);
        var now = _timeProvider.GetUtcNow();
        var idempotency = IdempotencyRecord.Start(key, requestHash, now);
        idempotency.Complete(operationalEvent.Id.ToString("D"), now);
        _store.AddEvent(operationalEvent);
        _store.AddIdempotency(idempotency);
        _store.AddOutbox(OutboxMessage.Create(
            "edge.operational-event-recorded",
            JsonSerializer.Serialize(command),
            new CorrelationId(command.CorrelationId),
            now));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("Operational event conflicts with an already committed operation.", exception);
        }
    }

    public async Task<IReadOnlyCollection<EdgeHealthDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var statuses = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return statuses.Select(Map).ToArray();
    }

    public async Task<PagedResult<EdgeHealthDto>> ListPageAsync(
        AccessScope accessScope,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accessScope);
        if (page <= 0 || pageSize is <= 0 or > 200)
        {
            throw new CoreValidationException("Page must be positive and pageSize must be between 1 and 200.");
        }

        var centers = accessScope.IsGlobal
            ? null
            : accessScope.AllowedCenterCodes.Select(x => new ExchangeCenterCode(x)).ToArray();
        var result = await _store.QueryPageAsync(
            centers,
            checked((page - 1) * pageSize),
            pageSize,
            cancellationToken).ConfigureAwait(false);
        return new PagedResult<EdgeHealthDto>(result.Items.Select(Map).ToArray(), result.TotalCount);
    }

    public async Task<EdgeHealthDto?> FindAsync(
        Guid edgeId,
        CancellationToken cancellationToken = default)
    {
        var status = await _store.FindAsync(new EdgeId(edgeId), cancellationToken).ConfigureAwait(false);
        return status is null ? null : Map(status);
    }

    private static EdgeHealthDto Map(EdgeHealthStatus status) =>
        new(
            status.EdgeId.Value,
            status.ExchangeCenterCode.Value,
            status.LastHeartbeat,
            status.SoftwareVersion,
            status.ConfigurationVersion,
            status.AvailableDiskBytes,
            status.ConnectionState,
            status.PendingCount,
            status.FailedCount,
            status.DeadLetterCount,
            status.LastSuccessfulSync);
}

public sealed class EdgeHealthMonitor : IEdgeHealthMonitor
{
    private readonly IEdgeHealthStore _store;
    private readonly TimeProvider _timeProvider;

    public EdgeHealthMonitor(IEdgeHealthStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<int> MarkStaleEdgesOfflineAsync(
        TimeSpan threshold,
        CancellationToken cancellationToken = default)
    {
        var statuses = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        var changed = statuses.Count(x => x.MarkOfflineWhenStale(_timeProvider.GetUtcNow(), threshold));
        if (changed > 0)
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return changed;
    }
}

public sealed class OperationalResultService : IOperationalResultService
{
    private readonly IOperationalResultStore _store;
    private readonly IRequestHasher _hasher;
    private readonly TimeProvider _timeProvider;

    public OperationalResultService(
        IOperationalResultStore store,
        IRequestHasher hasher,
        TimeProvider timeProvider)
    {
        _store = store;
        _hasher = hasher;
        _timeProvider = timeProvider;
    }

    public async Task<StorageResult> StoreAsync(
        OperationalResultCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = new IdempotencyKey(command.IdempotencyKey);
        var hash = _hasher.Hash(new
        {
            Operation = "operational-result.store",
            command.EdgeId,
            command.ExchangeCenterCode,
            IdempotencyKey = key.Value,
            command.ParcelBarcode,
            command.CallResult,
            command.Success,
            command.ErrorCode,
            command.ErrorMessage,
            command.CalledAt,
            command.RespondedAt,
            command.Attempts,
            command.FinalStatus
        });
        var existing = await _store.FindByIdempotencyKeyAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.IsReplay(hash))
            {
                throw new CoreConflictException("The idempotency key was already used with a different operational result.");
            }

            return new StorageResult(existing.Id.ToString("D"), true);
        }

        var result = OperationalResult.Create(
            new EdgeId(command.EdgeId),
            new ExchangeCenterCode(command.ExchangeCenterCode),
            key,
            hash,
            new CorrelationId(command.CorrelationId),
            string.IsNullOrWhiteSpace(command.ParcelBarcode) ? null : new ParcelBarcode(command.ParcelBarcode),
            command.CallResult,
            command.Success,
            command.ErrorCode,
            command.ErrorMessage,
            command.CalledAt,
            command.RespondedAt,
            command.Attempts,
            command.FinalStatus);
        var idempotency = IdempotencyRecord.Start(key, hash, _timeProvider.GetUtcNow());
        idempotency.Complete(result.Id.ToString("D"), _timeProvider.GetUtcNow());
        _store.AddResult(result);
        _store.AddIdempotency(idempotency);
        _store.AddOutbox(OutboxMessage.Create(
            "opresult.stored",
            JsonSerializer.Serialize(command),
            new CorrelationId(command.CorrelationId),
            _timeProvider.GetUtcNow()));
        try
        {
            await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (UniqueConstraintViolationException exception)
        {
            throw new CoreConflictException("The operational result conflicts with an already committed operation.", exception);
        }

        return new StorageResult(result.Id.ToString("D"), false);
    }
}
