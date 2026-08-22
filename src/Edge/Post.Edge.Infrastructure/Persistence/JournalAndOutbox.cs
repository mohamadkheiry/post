using Microsoft.EntityFrameworkCore;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Workers;

namespace Post.Edge.Infrastructure.Persistence;

internal sealed class OperationJournal(EdgeDbContext db) : IOperationJournal
{
    public async Task<TResponse?> GetResponseAsync<TResponse>(
        Guid operationId,
        string operationType,
        CancellationToken cancellationToken)
    {
        var entry = await GetAsync<TResponse>(operationId, operationType, cancellationToken);
        return entry is null ? default : entry.Response;
    }

    public async Task<OperationJournalEntry<TResponse>?> GetAsync<TResponse>(
        Guid operationId,
        string operationType,
        CancellationToken cancellationToken)
    {
        var row = await db.OperationJournal.SingleOrDefaultAsync(x =>
            x.OperationId == operationId && x.OperationType == operationType, cancellationToken);
        return row is null
            ? default
            : new OperationJournalEntry<TResponse>(
                row.RequestHash,
                PersistenceJson.Deserialize<TResponse>(row.ResponseJson));
    }

    public void AddResponse<TResponse>(
        Guid operationId,
        string operationType,
        TResponse response,
        DateTimeOffset createdAt,
        string? requestHash = null)
    {
        var row = db.OperationJournal.Local.SingleOrDefault(x =>
            x.OperationId == operationId && x.OperationType == operationType);
        if (row is null)
        {
            db.OperationJournal.Add(new OperationJournalRow
            {
                OperationId = operationId,
                OperationType = operationType,
                RequestHash = requestHash ?? throw new InvalidOperationException(
                    "A request hash is required for a new operation journal entry."),
                ResponseJson = PersistenceJson.Serialize(response),
                CreatedAt = createdAt
            });
            return;
        }

        if (requestHash is not null &&
            !string.Equals(row.RequestHash, requestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("An operation journal request hash cannot be changed.");
        row.ResponseJson = PersistenceJson.Serialize(response);
        row.CreatedAt = createdAt;
    }
}

internal sealed class OutboxWriter(EdgeDbContext db, IOutboxWakeSignal wakeSignal) : IOutboxWriter, IOutboxStatus
{
    public void Enqueue(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.Equals(message.Target, "DeadLetter", StringComparison.OrdinalIgnoreCase))
        {
            db.DeadLetters.Add(new DeadLetterRow
            {
                MessageId = message.MessageId,
                Target = message.Target,
                MessageType = message.MessageType,
                PayloadJson = message.PayloadJson,
                IdempotencyKey = message.IdempotencyKey,
                OccurredAt = message.OccurredAt,
                Attempts = 0,
                FailedAt = message.OccurredAt,
                Error = message.MessageType
            });
            return;
        }
        db.Outbox.Add(new OutboxRow
        {
            MessageId = message.MessageId,
            Target = message.Target,
            MessageType = message.MessageType,
            PayloadJson = message.PayloadJson,
            IdempotencyKey = message.IdempotencyKey,
            OccurredAt = message.OccurredAt,
            NextAttemptAt = message.NextAttemptAt
        });
        db.TrackPostSave(wakeSignal.Notify);
    }

    public async Task<bool> IsPendingAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (await db.Outbox.AnyAsync(
            row => row.IdempotencyKey == idempotencyKey && row.ProcessedAt == null,
            cancellationToken))
        {
            return true;
        }

        return await db.DeadLetters.AnyAsync(
            row => row.IdempotencyKey == idempotencyKey,
            cancellationToken);
    }
}
