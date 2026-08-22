using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Application.Services;
using Post.Edge.Domain.Parcels;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Persistence;

namespace Post.Edge.Infrastructure.Workers;

public sealed record AutoDispatchRunResult(int CandidateGroups, int Completed, int Failed);

public sealed class AutoDispatchService(
    EdgeDbContext db,
    IDispatchClosingService dispatches,
    IDeviceConfigurationRepository configurations,
    IClock clock,
    IOptions<EdgeAutoDispatchOptions> options)
{
    public async Task<AutoDispatchRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var policy = options.Value;
        if (!policy.Enabled) return new AutoDispatchRunResult(0, 0, 0);
        var snapshot = await configurations.GetAsync(cancellationToken);
        if (snapshot is null || !snapshot.AutoDispatchEnabled)
            return new AutoDispatchRunResult(0, 0, 0);
        var oldestAllowed = clock.UtcNow - TimeSpan.FromMinutes(snapshot.MaximumOpenBagMinutes);
        var undispatched = await db.Bags.AsNoTracking()
            .Where(bag => bag.DispatchId == null)
            .Select(bag => new { bag.DeviceId, bag.DestinationCode, bag.BagBarcode, bag.CreatedAt })
            .ToListAsync(cancellationToken);
        var candidates = undispatched
            .GroupBy(bag => (bag.DeviceId, bag.DestinationCode))
            .Where(group => group.Count() >= policy.MinimumBagCount || group.Min(bag => bag.CreatedAt) <= oldestAllowed)
            .Select(group => new
            {
                group.Key.DeviceId,
                group.Key.DestinationCode,
                BagBarcodes = group.OrderBy(bag => bag.CreatedAt).Select(bag => bag.BagBarcode).ToArray()
            })
            .ToArray();
        var filtered = candidates.Where(candidate =>
                (policy.DeviceIds.Count == 0 || policy.DeviceIds.Contains(candidate.DeviceId, StringComparer.Ordinal)) &&
                (policy.DestinationCodes.Count == 0 ||
                 policy.DestinationCodes.Contains(candidate.DestinationCode, StringComparer.Ordinal)))
            .ToArray();
        var completed = 0;
        foreach (var candidate in filtered)
        {
            var operationId = StableOperationId(candidate.DeviceId, candidate.DestinationCode, candidate.BagBarcodes);
            var result = await dispatches.CloseAsync(new CloseDispatchRequest(
                operationId,
                operationId,
                candidate.DeviceId,
                candidate.DestinationCode,
                candidate.BagBarcodes), cancellationToken);
            if (result.IsSuccess) completed++;
        }
        return new AutoDispatchRunResult(filtered.Length, completed, filtered.Length - completed);
    }

    private static Guid StableOperationId(
        string deviceId,
        string destinationCode,
        IReadOnlyCollection<string> bagBarcodes)
    {
        var material = string.Join('|',
            new[] { deviceId, destinationCode }.Concat(bagBarcodes.Order(StringComparer.Ordinal)));
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16));
    }
}

public sealed record LocalCleanupResult(
    int Parcels,
    int Bags,
    int Dispatches,
    int OutboxMessages,
    int TerminalBagClosures,
    int OperationJournalEntries,
    int FinalizedExternalOperations);

public sealed class LocalCleanupService(
    EdgeDbContext db,
    IClock clock,
    IOptions<EdgeWorkerOptions> options)
{
    public async Task<LocalCleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.CleanupEnabled) return new LocalCleanupResult(0, 0, 0, 0, 0, 0, 0);
        var cutoff = clock.UtcNow - settings.RetentionPeriod;
        var idempotencyCutoff = clock.UtcNow - settings.IdempotencyRetentionPeriod;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var candidateDispatches = await db.Dispatches.AsNoTracking()
            .Where(dispatch => dispatch.CoreConfirmed && dispatch.CreatedAt < cutoff)
            .ToArrayAsync(cancellationToken);
        var candidateDispatchIds = candidateDispatches.Select(dispatch => dispatch.DispatchId).ToArray();
        var candidateBags = await db.Bags.AsNoTracking()
            .Where(bag => bag.DispatchId != null && candidateDispatchIds.Contains(bag.DispatchId))
            .ToArrayAsync(cancellationToken);
        var candidateBagBarcodes = candidateBags.Select(bag => bag.BagBarcode).ToArray();
        var candidateParcels = await db.Parcels.AsNoTracking()
            .Where(parcel => parcel.BagBarcode != null && candidateBagBarcodes.Contains(parcel.BagBarcode))
            .ToArrayAsync(cancellationToken);
        var eligibleBagBarcodes = candidateBags
            .Where(bag => IsCompleteConfirmedBag(bag, candidateParcels, cutoff))
            .Select(bag => bag.BagBarcode)
            .ToHashSet(StringComparer.Ordinal);
        var eligibleDispatchIds = candidateDispatches
            .Where(dispatch => IsCompleteConfirmedDispatch(dispatch, candidateBags, eligibleBagBarcodes))
            .Select(dispatch => dispatch.DispatchId)
            .ToHashSet(StringComparer.Ordinal);
        var confirmedBagBarcodes = candidateBags
            .Where(bag => bag.DispatchId is not null &&
                          eligibleDispatchIds.Contains(bag.DispatchId) &&
                          eligibleBagBarcodes.Contains(bag.BagBarcode))
            .Select(bag => bag.BagBarcode)
            .ToArray();
        var confirmedDispatchIds = eligibleDispatchIds.ToArray();
        var parcelIds = candidateParcels
            .Where(parcel => parcel.BagBarcode is not null &&
                             confirmedBagBarcodes.Contains(parcel.BagBarcode, StringComparer.Ordinal))
            .Select(parcel => parcel.ReadId)
            .ToArray();

        await db.InboundImages.Where(image => parcelIds.Contains(image.ReadId)).ExecuteDeleteAsync(cancellationToken);
        await db.InboundEvidence.Where(evidence => parcelIds.Contains(evidence.ReadId)).ExecuteDeleteAsync(cancellationToken);
        var parcels = await db.Parcels.Where(parcel => parcelIds.Contains(parcel.ReadId))
            .ExecuteDeleteAsync(cancellationToken);
        var bags = await db.Bags.Where(bag => confirmedBagBarcodes.Contains(bag.BagBarcode))
            .ExecuteDeleteAsync(cancellationToken);
        var dispatches = await db.Dispatches
            .Where(dispatch => confirmedDispatchIds.Contains(dispatch.DispatchId))
            .ExecuteDeleteAsync(cancellationToken);
        var outbox = await db.Outbox
            .Where(message => message.ProcessedAt != null && message.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var terminalBagClosures = await db.PendingBagClosures
            .Where(closure =>
                closure.State != (int)Post.Edge.Domain.Bags.PendingBagClosureState.Pending &&
                closure.CreatedAt < idempotencyCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        var operationJournalEntries = await PruneOperationJournalAsync(idempotencyCutoff, cancellationToken);
        var finalizedExternalOperations = await db.ExternalOperationSagas
            .Where(operation => operation.Finalized && operation.UpdatedAt < idempotencyCutoff)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LocalCleanupResult(
            parcels,
            bags,
            dispatches,
            outbox,
            terminalBagClosures,
            operationJournalEntries,
            finalizedExternalOperations);
    }

    private static bool IsCompleteConfirmedBag(
        BagRow bag,
        IReadOnlyCollection<ParcelRow> parcels,
        DateTimeOffset cutoff)
    {
        if (!bag.CoreConfirmed || bag.CreatedAt >= cutoff || bag.DispatchId is null) return false;
        var memberBarcodes = TryReadStringArray(bag.MemberBarcodesJson);
        if (memberBarcodes is null || memberBarcodes.Length == 0) return false;
        var members = memberBarcodes.ToHashSet(StringComparer.Ordinal);
        var bagParcels = parcels.Where(parcel =>
            string.Equals(parcel.BagBarcode, bag.BagBarcode, StringComparison.Ordinal)).ToArray();
        return bagParcels.Length == members.Count &&
               bagParcels.All(parcel =>
                   members.Contains(parcel.CanonicalBarcode) &&
                   parcel.CoreSynced &&
                   parcel.State == (int)EdgeParcelState.Bagged &&
                   parcel.ReadTimestamp < cutoff);
    }

    private static bool IsCompleteConfirmedDispatch(
        DispatchRow dispatch,
        IReadOnlyCollection<BagRow> bags,
        HashSet<string> eligibleBagBarcodes)
    {
        var bagBarcodes = TryReadStringArray(dispatch.BagBarcodesJson);
        if (bagBarcodes is null || bagBarcodes.Length == 0) return false;
        var members = bagBarcodes.ToHashSet(StringComparer.Ordinal);
        var dispatchBags = bags.Where(bag =>
            string.Equals(bag.DispatchId, dispatch.DispatchId, StringComparison.Ordinal)).ToArray();
        return dispatchBags.Length == members.Count &&
               dispatchBags.All(bag =>
                   members.Contains(bag.BagBarcode) && eligibleBagBarcodes.Contains(bag.BagBarcode));
    }

    private static string[]? TryReadStringArray(string json)
    {
        try
        {
            return PersistenceJson.Deserialize<string[]>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<int> PruneOperationJournalAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        var candidates = await db.OperationJournal
            .Where(operation => operation.CreatedAt < cutoff)
            .ToArrayAsync(cancellationToken);
        if (candidates.Length == 0) return 0;

        var operationIds = candidates.Select(candidate => candidate.OperationId).Distinct().ToArray();
        var pendingInboundIds = (await db.PendingInbounds
                .Where(item => operationIds.Contains(item.OperationId))
                .Select(item => item.OperationId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();
        var pendingBagIds = (await db.PendingBagClosures
                .Where(item => operationIds.Contains(item.LocalOperationId))
                .Select(item => item.LocalOperationId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();
        var bagIds = (await db.Bags
                .Where(item => operationIds.Contains(item.LocalOperationId))
                .Select(item => item.LocalOperationId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();
        var dispatchIds = (await db.Dispatches
                .Where(item => operationIds.Contains(item.LocalOperationId))
                .Select(item => item.LocalOperationId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();
        var referencedReadIds = candidates
            .Select(TryReadId)
            .Where(readId => readId.HasValue && readId.Value != Guid.Empty)
            .Select(readId => readId!.Value)
            .Distinct()
            .ToArray();
        var parcelReadIds = (await db.Parcels
                .Where(parcel => referencedReadIds.Contains(parcel.ReadId))
                .Select(parcel => parcel.ReadId)
                .ToArrayAsync(cancellationToken))
            .ToHashSet();

        var removable = candidates.Where(candidate => candidate.OperationType switch
        {
            "parcel.inbound" => !pendingInboundIds.Contains(candidate.OperationId) &&
                                IsResponseOrphaned(candidate, parcelReadIds),
            "destination.assign" => IsResponseOrphaned(candidate, parcelReadIds),
            "bag.close" => !pendingBagIds.Contains(candidate.OperationId) &&
                           !bagIds.Contains(candidate.OperationId),
            "dispatch.close" => !dispatchIds.Contains(candidate.OperationId),
            _ => false
        }).ToArray();
        db.OperationJournal.RemoveRange(removable);
        await db.SaveChangesAsync(cancellationToken);
        return removable.Length;
    }

    private static bool IsResponseOrphaned(
        OperationJournalRow operation,
        HashSet<Guid> existingReadIds)
    {
        var readId = TryReadId(operation);
        return readId.HasValue &&
               (readId.Value == Guid.Empty || !existingReadIds.Contains(readId.Value));
    }

    private static Guid? TryReadId(OperationJournalRow operation)
    {
        try
        {
            return operation.OperationType switch
            {
                "parcel.inbound" =>
                    PersistenceJson.Deserialize<InboundParcelResponse>(operation.ResponseJson).ReadId ?? Guid.Empty,
                "destination.assign" =>
                    PersistenceJson.Deserialize<DestinationAssignResponse>(operation.ResponseJson).ReadId,
                _ => null
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

internal sealed class AutoDispatchWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EdgeAutoDispatchOptions> options,
    ILogger<AutoDispatchWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4105, "AutoDispatchFailed"),
        "Automatic Edge dispatch scan failed and will be retried.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(options.Value.ScanInterval);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<AutoDispatchService>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                LogFailure(logger, null);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

internal sealed class LocalCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EdgeWorkerOptions> options,
    ILogger<LocalCleanupWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogFailure = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(4106, "LocalCleanupFailed"),
        "Edge local retention cleanup failed and will be retried.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.CleanupEnabled) return;
        using var timer = new PeriodicTimer(options.Value.CleanupInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<LocalCleanupService>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                LogFailure(logger, null);
            }
        }
    }
}
