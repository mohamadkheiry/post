using Microsoft.EntityFrameworkCore;
using Post.Edge.Application.Ports;

namespace Post.Edge.Infrastructure.Persistence;

internal sealed class ExternalOperationSagaRepository(EdgeDbContext db) : IExternalOperationSagaRepository
{
    public async Task<ExternalOperationSaga?> GetAsync(
        Guid operationId,
        string operationType,
        CancellationToken cancellationToken)
    {
        var row = await db.ExternalOperationSagas.AsNoTracking().SingleOrDefaultAsync(candidate =>
            candidate.OperationId == operationId && candidate.OperationType == operationType,
            cancellationToken);
        return row is null ? null : ToDomain(row);
    }

    public void AddIntent(ExternalOperationSaga saga)
    {
        ArgumentNullException.ThrowIfNull(saga);
        db.ExternalOperationSagas.Add(new ExternalOperationSagaRow
        {
            OperationId = saga.OperationId,
            OperationType = saga.OperationType,
            RequestHash = saga.RequestHash,
            IntentJson = saga.IntentJson,
            ReceiptJson = saga.ReceiptJson,
            Finalized = saga.Finalized,
            CreatedAt = saga.CreatedAt,
            UpdatedAt = saga.UpdatedAt
        });
    }

    public async Task StoreReceiptAsync(
        Guid operationId,
        string operationType,
        string receiptJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptJson);
        var row = await FindTrackedAsync(operationId, operationType, cancellationToken);
        if (row.Finalized) throw new InvalidOperationException("A finalized external operation cannot accept a receipt.");
        if (row.ReceiptJson is not null && !string.Equals(row.ReceiptJson, receiptJson, StringComparison.Ordinal))
            throw new InvalidOperationException("An external operation receipt cannot be changed.");
        row.ReceiptJson = receiptJson;
        row.UpdatedAt = updatedAt;
    }

    public async Task MarkFinalizedAsync(
        Guid operationId,
        string operationType,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var row = await FindTrackedAsync(operationId, operationType, cancellationToken);
        if (row.ReceiptJson is null)
            throw new InvalidOperationException("An external operation cannot finalize without a receipt.");
        row.Finalized = true;
        row.UpdatedAt = updatedAt;
    }

    public async Task<ExternalOperationSagaStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var unresolved = db.ExternalOperationSagas.AsNoTracking().Where(candidate => !candidate.Finalized);
        var count = await unresolved.CountAsync(cancellationToken);
        var oldest = count == 0
            ? (DateTimeOffset?)null
            : await unresolved.MinAsync(candidate => candidate.CreatedAt, cancellationToken);
        return new ExternalOperationSagaStatus(count, oldest);
    }

    private async Task<ExternalOperationSagaRow> FindTrackedAsync(
        Guid operationId,
        string operationType,
        CancellationToken cancellationToken) =>
        db.ExternalOperationSagas.Local.SingleOrDefault(candidate =>
            candidate.OperationId == operationId && candidate.OperationType == operationType)
        ?? await db.ExternalOperationSagas.SingleOrDefaultAsync(candidate =>
               candidate.OperationId == operationId && candidate.OperationType == operationType,
               cancellationToken)
        ?? throw new InvalidOperationException("The external operation intent was not found.");

    private static ExternalOperationSaga ToDomain(ExternalOperationSagaRow row) => new(
        row.OperationId,
        row.OperationType,
        row.RequestHash,
        row.IntentJson,
        row.ReceiptJson,
        row.Finalized,
        row.CreatedAt,
        row.UpdatedAt);
}
