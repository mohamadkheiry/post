using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Post.Core.Application.Abstractions;
using Post.Core.Application.Common;
using Post.Core.Application.Contracts;
using Post.Core.Domain.Common;
using Post.Core.Domain.Parcels;
using Post.Core.Infrastructure.Messaging;

namespace Post.Core.Infrastructure.Hosting;

public sealed record PendingAttachmentReconciliationOptions
{
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    public int MaximumAttempts { get; init; } = 10;

    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromHours(24);

    public int BatchSize { get; init; } = 50;

    public string DeadLetterTopic { get; init; } = "image.metadata.pending.dlq";
}

public sealed record PendingAttachmentReconciliationResult(
    int Examined,
    int Linked,
    int ReplayCleaned,
    int Deferred,
    int DeadLettered);

public interface IPendingAttachmentReconciliationProcessor
{
    Task<PendingAttachmentReconciliationResult> ProcessBatchAsync(
        CancellationToken cancellationToken = default);
}

public sealed class PendingAttachmentReconciliationProcessor : IPendingAttachmentReconciliationProcessor
{
    private readonly IParcelLifecycleStore _store;
    private readonly IAttachmentLinkingService _linkingService;
    private readonly IKafkaCompatibleProducer _producer;
    private readonly TimeProvider _timeProvider;
    private readonly PendingAttachmentReconciliationOptions _options;

    public PendingAttachmentReconciliationProcessor(
        IParcelLifecycleStore store,
        IAttachmentLinkingService linkingService,
        IKafkaCompatibleProducer producer,
        TimeProvider timeProvider,
        PendingAttachmentReconciliationOptions options)
    {
        _store = store;
        _linkingService = linkingService;
        _producer = producer;
        _timeProvider = timeProvider;
        _options = options;
    }

    public async Task<PendingAttachmentReconciliationResult> ProcessBatchAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var pendingItems = await _store.GetDuePendingAttachmentsAsync(
            now,
            _options.BatchSize,
            cancellationToken).ConfigureAwait(false);
        var linked = 0;
        var replayCleaned = 0;
        var deferred = 0;
        var deadLettered = 0;

        foreach (var pending in pendingItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pending.IsExpired(now, _options.MaximumAge))
            {
                deadLettered += await DeadLetterAsync(
                    pending,
                    "MaximumAgeExceeded",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (pending.Attempts >= _options.MaximumAttempts)
            {
                deadLettered += await DeadLetterAsync(
                    pending,
                    "MaximumAttemptsExceeded",
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var command = JsonSerializer.Deserialize<ImageMetadataCommand>(pending.PayloadJson)
                    ?? throw new JsonException("Pending attachment payload is empty.");
                ValidateEnvelope(pending, command);
                if (await _linkingService.LinkAsync(command, cancellationToken).ConfigureAwait(false))
                {
                    linked++;
                    continue;
                }

                var inbox = await _store.FindInboxMessageAsync(pending.EventId, cancellationToken)
                    .ConfigureAwait(false);
                if (inbox is not null)
                {
                    _store.RemovePendingAttachment(pending);
                    await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    replayCleaned++;
                    continue;
                }

                await DeferAsync(pending, "ReadingNotAvailable", cancellationToken).ConfigureAwait(false);
                deferred++;
            }
            catch (Exception exception) when (
                exception is JsonException or
                CoreValidationException or
                CoreAuthorizationException or
                CoreConflictException or
                DomainRuleException)
            {
                deadLettered += await DeadLetterAsync(
                    pending,
                    exception.GetType().Name,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new PendingAttachmentReconciliationResult(
            pendingItems.Count,
            linked,
            replayCleaned,
            deferred,
            deadLettered);
    }

    private static void ValidateEnvelope(PendingAttachment pending, ImageMetadataCommand command)
    {
        if (command.EventId != pending.EventId ||
            command.ReadingRecordId != pending.ReadingRecordId ||
            !string.Equals(command.ParcelBarcode, pending.ParcelBarcode.Value, StringComparison.Ordinal) ||
            !string.Equals(command.ObjectKey, pending.ObjectKey, StringComparison.Ordinal) ||
            !string.Equals(command.ContentType, pending.ContentType, StringComparison.Ordinal) ||
            !string.Equals(command.AttachmentType, pending.AttachmentType, StringComparison.Ordinal))
        {
            throw new CoreValidationException("Pending attachment envelope does not match its durable index fields.");
        }
    }

    private async Task DeferAsync(
        PendingAttachment pending,
        string errorType,
        CancellationToken cancellationToken)
    {
        pending.ScheduleRetry(
            _timeProvider.GetUtcNow(),
            CalculateRetryDelay(pending.Attempts, _options.BaseRetryDelay, _options.MaximumRetryDelay),
            errorType);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> DeadLetterAsync(
        PendingAttachment pending,
        string errorType,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var record = new
        {
            PendingAttachmentId = pending.Id,
            pending.EventId,
            pending.RequestHash,
            OriginalPayload = pending.PayloadJson,
            ErrorType = errorType,
            pending.Attempts,
            pending.CreatedAt,
            DeadLetteredAt = now
        };
        var receipt = await _producer.ProduceAsync(
            _options.DeadLetterTopic,
            pending.EventId.ToString("N"),
            JsonSerializer.SerializeToUtf8Bytes(record),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pending-attachment-id"] = pending.Id.ToString("D"),
                ["event-id"] = pending.EventId.ToString("D"),
                ["error-type"] = errorType
            },
            cancellationToken).ConfigureAwait(false);
        if (!receipt.Persisted)
        {
            await DeferAsync(pending, "DeadLetterPublishNotPersisted", cancellationToken).ConfigureAwait(false);
            return 0;
        }

        pending.MarkDeadLettered(now, errorType);
        await _store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return 1;
    }

    private static TimeSpan CalculateRetryDelay(
        int attempts,
        TimeSpan baseDelay,
        TimeSpan maximumDelay)
    {
        var multiplier = Math.Pow(2, Math.Min(attempts, 30));
        var ticks = Math.Min(maximumDelay.Ticks, baseDelay.Ticks * multiplier);
        return TimeSpan.FromTicks(checked((long)ticks));
    }
}

public sealed class CorePendingAttachmentReconciliationBackgroundService : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, int, Exception?> LogBatch =
        LoggerMessage.Define<int, int, int, int>(
            LogLevel.Information,
            new EventId(6, "CorePendingAttachmentReconciliationBatch"),
            "Pending attachment reconciliation examined {Examined}, linked {Linked}, deferred {Deferred}, and dead-lettered {DeadLettered} records.");

    private static readonly Action<ILogger, string, Exception?> LogFailure =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(7, "CorePendingAttachmentReconciliationFailure"),
            "Pending attachment reconciliation failed with {ExceptionType} and will resume on the next polling interval.");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly PendingAttachmentReconciliationOptions _options;
    private readonly ILogger<CorePendingAttachmentReconciliationBackgroundService> _logger;

    public CorePendingAttachmentReconciliationBackgroundService(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        PendingAttachmentReconciliationOptions options,
        ILogger<CorePendingAttachmentReconciliationBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<PendingAttachmentReconciliationResult?> ReconcileOnceAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredService<IPendingAttachmentReconciliationProcessor>();
            var result = await processor.ProcessBatchAsync(cancellationToken).ConfigureAwait(false);
            if (result.Examined > 0)
            {
                LogBatch(
                    _logger,
                    result.Examined,
                    result.Linked + result.ReplayCleaned,
                    result.Deferred,
                    result.DeadLettered,
                    null);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is CoreApplicationException or
            DbUpdateException or
            TimeoutException or
            InvalidOperationException)
        {
            LogFailure(_logger, exception.GetType().Name, null);
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_options.PollingInterval, _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
