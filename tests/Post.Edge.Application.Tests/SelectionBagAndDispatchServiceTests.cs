using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Application.Services;
using Post.Edge.Domain.Bags;
using Post.Edge.Domain.ValueObjects;

namespace Post.Edge.Application.Tests;

public sealed class SelectionBagAndDispatchServiceTests
{
    private const string Barcode1 = "123450000000005954400001";
    private const string Barcode2 = "123450000000005954400002";
    private const string Barcode3 = "123450000000005954400003";

    [Fact]
    public async Task SelectionIgnoresInformationalCountAndHonorsInclusiveLastMarker()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await host.SeedAssignedParcelAsync(Barcode1, now);
        var second = await host.SeedAssignedParcelAsync(Barcode2, now.AddSeconds(1));
        await host.SeedAssignedParcelAsync(Barcode3, now.AddSeconds(2));

        var all = await host.InvokeAsync<IParcelSelectionService, IReadOnlyList<SelectedParcel>>(x =>
            x.SelectAsync(new ParcelSelectionFilter("device-1", "123", null, null, 1, null, null)));
        var throughSecond = await host.InvokeAsync<IParcelSelectionService, IReadOnlyList<SelectedParcel>>(x =>
            x.SelectAsync(new ParcelSelectionFilter("device-1", "123", second.ReadId, Barcode2, 999, null, null)));

        Assert.Equal(3, all.Count);
        Assert.Equal(2, throughSecond.Count);
        Assert.Equal(second.ReadId, throughSecond[^1].ReadId);
        var filteredOut = await host.InvokeAsync<IParcelSelectionService, IReadOnlyList<SelectedParcel>>(x =>
            x.SelectAsync(new ParcelSelectionFilter(
                "device-1", "123", null, null, 999, null, null,
                ChuteIds: ["OTHER"], ParcelTypes: ["Parcel"], ServiceTypes: ["Express"])));
        Assert.Empty(filteredOut);
    }

    [Fact]
    public async Task BagClosureComputesNpmqPersistsSuccessAndReplaysResponse()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(new Dictionary<string, string?>
        {
            ["Edge:Mock:BagFailureBarcodes:0"] = Barcode2
        });
        var now = DateTimeOffset.UtcNow;
        await host.SeedAssignedParcelAsync(Barcode1, now);
        await host.SeedAssignedParcelAsync(Barcode2, now.AddSeconds(1), deferred: true);
        var operationId = Guid.NewGuid();
        var request = BagRequest(operationId);

        var result = await host.InvokeAsync<IBagClosingService, CloseBagResponse>(x => x.CloseAsync(request));
        var replay = await host.InvokeAsync<IBagClosingService, CloseBagResponse>(x => x.CloseAsync(request));
        var conflict = await host.InvokeAsync<IBagClosingService, CloseBagResponse>(x =>
            x.CloseAsync(request with { SealNumber = "SEAL-CHANGED" }));

        Assert.Equal(BagCloseResult.Completed, result.Result);
        Assert.NotNull(result.BagBarcode);
        Assert.NotNull(result.LabelPayload);
        Assert.Equal(1, result.Counts.N);
        Assert.Equal(1, result.Counts.P);
        Assert.Equal(0, result.Counts.M);
        Assert.Equal(1, result.Counts.Q);
        Assert.Equal(1, result.Counts.SuccessCount);
        Assert.Single(result.Errors);
        Assert.Equal(result.OperationId, replay.OperationId);
        Assert.Equal(result.BagBarcode, replay.BagBarcode);
        Assert.Equal(result.Result, replay.Result);
        Assert.Equal(BagCloseResult.Error, conflict.Result);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.ErrorCode);
    }

    [Fact]
    public async Task BagClosureReturnsNoEligibleAndRequiresManualReconciliationForUnknownCommit()
    {
        await using var emptyHost = await EdgeApplicationTestHost.CreateAsync();
        var empty = await emptyHost.InvokeAsync<IBagClosingService, CloseBagResponse>(x =>
            x.CloseAsync(BagRequest(Guid.NewGuid())));
        Assert.Equal(BagCloseResult.NoEligibleParcel, empty.Result);

        await using var offlineHost = await EdgeApplicationTestHost.CreateAsync(new Dictionary<string, string?>
        {
            ["Edge:Mock:BagServiceUnavailable"] = "true"
        });
        await offlineHost.SeedAssignedParcelAsync(Barcode1, DateTimeOffset.UtcNow);
        var result = await offlineHost.InvokeAsync<IBagClosingService, CloseBagResponse>(x =>
            x.CloseAsync(BagRequest(Guid.NewGuid())));
        var work = await offlineHost.InvokeAsync<IPendingWorkService, PendingWorkResult>(x =>
            x.ProcessBagClosuresAsync(10));

        Assert.Equal(BagCloseResult.Error, result.Result);
        Assert.Equal("MANUAL_RECONCILIATION_REQUIRED", result.ErrorCode);
        Assert.Equal(0, work.Processed);
    }

    [Fact]
    public async Task BagClosureUsesDistinctAllParcelsFailedResult()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(new Dictionary<string, string?>
        {
            ["Edge:Mock:BagFailureBarcodes:0"] = Barcode1
        });
        await host.SeedAssignedParcelAsync(Barcode1, DateTimeOffset.UtcNow);

        var result = await host.InvokeAsync<IBagClosingService, CloseBagResponse>(x =>
            x.CloseAsync(BagRequest(Guid.NewGuid())));

        Assert.Equal(BagCloseResult.AllParcelsFailed, result.Result);
        Assert.Equal(1, result.Counts.M);
    }

    [Fact]
    public async Task AllParcelsFailedDoesNotRequirePostalBagBarcode()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IPostalBagClient>();
            services.AddSingleton<IPostalBagClient, AllFailedWithoutBagBarcodeClient>();
        });
        await host.SeedAssignedParcelAsync(Barcode1, DateTimeOffset.UtcNow);

        var result = await host.InvokeAsync<IBagClosingService, CloseBagResponse>(service =>
            service.CloseAsync(BagRequest(Guid.NewGuid())));

        Assert.Equal(BagCloseResult.AllParcelsFailed, result.Result);
        Assert.Null(result.BagBarcode);
        Assert.Equal(1, result.Counts.M);
        Assert.Equal(0, result.Counts.SuccessCount);
        Assert.Single(result.Errors);
    }

    [Fact]
    public async Task InconsistentPostalFailureSetReturnsSafeProtocolError()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IPostalBagClient>();
            services.AddSingleton<IPostalBagClient, InconsistentFailureSetClient>();
        });
        await host.SeedAssignedParcelAsync(Barcode1, DateTimeOffset.UtcNow);

        var result = await host.InvokeAsync<IBagClosingService, CloseBagResponse>(service =>
            service.CloseAsync(BagRequest(Guid.NewGuid())));

        Assert.Equal(BagCloseResult.Error, result.Result);
        Assert.Equal("POSTAL_PROTOCOL_ERROR", result.ErrorCode);
        Assert.DoesNotContain(Barcode1, result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BagClosureStoresSafeErrorForInvalidDestination()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();

        var result = await host.InvokeAsync<IBagClosingService, CloseBagResponse>(x =>
            x.CloseAsync(BagRequest(Guid.NewGuid()) with { DestinationCode = "invalid" }));

        Assert.Equal(BagCloseResult.Error, result.Result);
        Assert.Equal("INVALID_DESTINATION", result.ErrorCode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task PendingBagClosureDeadLettersWhenOriginalParcelIsMissing()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPendingBagClosureRepository>();
            repository.Add(PendingBagClosure.Create(
                Guid.NewGuid(),
                "device-1",
                PostalCenterCode.Create("123"),
                [Guid.NewGuid()],
                "SEAL-1",
                "Road",
                DateTimeOffset.UtcNow.AddSeconds(-1)));
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await host.InvokeAsync<IPendingWorkService, PendingWorkResult>(x =>
            x.ProcessBagClosuresAsync(10));

        Assert.Equal(1, result.DeadLettered);
        Assert.Equal(1, result.Processed);
    }

    [Fact]
    public async Task PendingBagPermanentFailureReplacesCachedLabelPendingResponse()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IPostalBagClient>();
            services.AddSingleton<IPostalBagClient, RejectedBagClient>();
        });
        var parcel = await host.SeedAssignedParcelAsync(Barcode1, DateTimeOffset.UtcNow);
        var operationId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IPendingBagClosureRepository>().Add(
                PendingBagClosure.Create(
                    operationId,
                    "device-1",
                    PostalCenterCode.Create("123"),
                    [parcel.ReadId],
                    "SEAL-1",
                    "Road",
                    DateTimeOffset.UtcNow.AddSeconds(-1)));
            scope.ServiceProvider.GetRequiredService<IOperationJournal>().AddResponse(
                operationId,
                "bag.close",
                new CloseBagResponse(
                    Guid.NewGuid(), operationId, BagCloseResult.LabelPending, null, null,
                    new BagCounts(1, 0, 0, 0), [], "POSTAL_PENDING", "Postal work is pending."),
                DateTimeOffset.UtcNow,
                "TEST-REQUEST-HASH");
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var work = await host.InvokeAsync<IPendingWorkService, PendingWorkResult>(service =>
            service.ProcessBagClosuresAsync(10));
        var replay = await host.InvokeAsync<IOperationJournal, CloseBagResponse?>(journal =>
            journal.GetResponseAsync<CloseBagResponse>(operationId, "bag.close", default));

        Assert.Equal(1, work.DeadLettered);
        Assert.NotNull(replay);
        Assert.Equal(BagCloseResult.Error, replay.Result);
        Assert.Equal("POSTAL_REJECTED", replay.ErrorCode);
        Assert.Equal("Postal rejected pending bag closure.", replay.ErrorMessage);
    }

    [Fact]
    public async Task PendingBagCompletionCreatesCorePayloadAndCanBeResolvedByOperationId()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        var parcel = await host.SeedAssignedParcelAsync(Barcode1, DateTimeOffset.UtcNow);
        var operationId = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IPendingBagClosureRepository>().Add(
                PendingBagClosure.Create(
                    operationId,
                    "device-1",
                    PostalCenterCode.Create("123"),
                    [parcel.ReadId],
                    "SEAL-PENDING",
                    "Road",
                    DateTimeOffset.UtcNow.AddSeconds(-1)));
            await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>()
                .SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var before = await host.InvokeAsync<IPendingBagClosureRepository, PendingBagClosure?>(repository =>
            repository.FindByOperationIdAsync(operationId, TestContext.Current.CancellationToken));
        var work = await host.InvokeAsync<IPendingWorkService, PendingWorkResult>(service =>
            service.ProcessBagClosuresAsync(10));
        var bag = await host.InvokeAsync<IEdgeBagRepository, EdgeBag?>(repository =>
            repository.FindByOperationIdAsync(operationId, TestContext.Current.CancellationToken));
        var corePayloadPending = await host.InvokeAsync<IOutboxStatus, bool>(status =>
            status.IsPendingAsync($"bag:{operationId:N}", TestContext.Current.CancellationToken));

        Assert.NotNull(before);
        Assert.Equal(1, work.Completed);
        Assert.NotNull(bag);
        Assert.Equal(Barcode1, bag.MemberBarcodes.Single());
        Assert.True(corePayloadPending);
    }

    [Fact]
    public async Task BagClosureResumesDurableReceiptAfterLabelCrashWithoutRepeatingPostalCall()
    {
        var postal = new CountingBagClient();
        var labels = new FailOnceLabelGenerator();
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IPostalBagClient>();
            services.AddSingleton<IPostalBagClient>(postal);
            services.RemoveAll<IBagLabelGenerator>();
            services.AddSingleton<IBagLabelGenerator>(labels);
        });
        await host.SeedAssignedParcelAsync(Barcode1, DateTimeOffset.UtcNow);
        var request = BagRequest(Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.InvokeAsync<IBagClosingService, CloseBagResponse>(service => service.CloseAsync(request)));
        var recovered = await host.InvokeAsync<IBagClosingService, CloseBagResponse>(
            service => service.CloseAsync(request));

        Assert.Equal(BagCloseResult.Completed, recovered.Result);
        Assert.Equal("BAG-RECOVERED", recovered.BagBarcode);
        Assert.Equal(1, postal.Calls);
        Assert.Equal(2, labels.Calls);
    }

    private sealed class AllFailedWithoutBagBarcodeClient : IPostalBagClient
    {
        public Task<PostalBagResult> CloseAsync(PostalBagCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PostalBagResult(
                PostalBagStatus.Completed,
                null,
                command.Parcels.Select(parcel => new PostalParcelFailure(
                    parcel.ReadId,
                    "REJECTED",
                    "Postal rejected the parcel.",
                    null)).ToArray(),
                null,
                null));
        }
    }

    private sealed class InconsistentFailureSetClient : IPostalBagClient
    {
        public Task<PostalBagResult> CloseAsync(PostalBagCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = new PostalParcelFailure(Guid.NewGuid(), "BAD", "Unknown parcel failure.", null);
            return Task.FromResult(new PostalBagResult(
                PostalBagStatus.Completed,
                "BAG-INVALID",
                [failure, failure],
                null,
                null));
        }
    }

    private sealed class RejectedBagClient : IPostalBagClient
    {
        public Task<PostalBagResult> CloseAsync(PostalBagCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PostalBagResult(
                PostalBagStatus.Failed,
                null,
                [],
                "POSTAL_REJECTED",
                "Postal rejected pending bag closure."));
        }
    }

    [Fact]
    public async Task DispatchClosesEligibleBagsIdempotentlyAndRejectsReassignment()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        await host.SeedBagAsync("BAG-1");
        var request = new CloseDispatchRequest(
            Guid.NewGuid(), Guid.NewGuid(), "device-1", "123");

        var success = await host.InvokeAsync<IDispatchClosingService, CloseDispatchResponse>(x => x.CloseAsync(request));
        var replay = await host.InvokeAsync<IDispatchClosingService, CloseDispatchResponse>(x => x.CloseAsync(request));
        var conflict = await host.InvokeAsync<IDispatchClosingService, CloseDispatchResponse>(x =>
            x.CloseAsync(request with { DestinationCode = "999" }));
        var reassignment = await host.InvokeAsync<IDispatchClosingService, CloseDispatchResponse>(x =>
            x.CloseAsync(request with { CorrelationId = Guid.NewGuid(), OperationId = Guid.NewGuid() }));

        Assert.Equal(1, success.Status);
        Assert.Equal(success.OperationId, replay.OperationId);
        Assert.Equal(success.DispatchId, replay.DispatchId);
        Assert.Equal(success.Status, replay.Status);
        Assert.Equal(1, success.TotalParcels);
        Assert.Empty(success.ProblemParcels);
        Assert.Equal(2, conflict.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.ErrorCode);
        Assert.Equal(2, reassignment.Status);
        Assert.Equal("NO_UNDISPATCHED_BAG", reassignment.ErrorCode);
    }

    [Fact]
    public async Task DispatchReturnsImmediateErrorWhenPostalServiceIsUnavailable()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(new Dictionary<string, string?>
        {
            ["Edge:Mock:DispatchServiceUnavailable"] = "true"
        });
        await host.SeedBagAsync("BAG-2");

        var response = await host.InvokeAsync<IDispatchClosingService, CloseDispatchResponse>(x =>
            x.CloseAsync(new CloseDispatchRequest(
                Guid.NewGuid(), Guid.NewGuid(), "device-1", "123", ["BAG-2"])));

        Assert.Equal(2, response.Status);
        Assert.Equal("MOCK_UNAVAILABLE", response.ErrorCode);
    }

    [Fact]
    public async Task DispatchResumesDurableReceiptAfterLocalCrashWithoutRepeatingPostalCall()
    {
        var postal = new CountingDispatchClient();
        var outbox = new ThrowOnceOutboxWriter();
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IPostalDispatchClient>();
            services.AddSingleton<IPostalDispatchClient>(postal);
            services.RemoveAll<IOutboxWriter>();
            services.AddSingleton<IOutboxWriter>(outbox);
        });
        await host.SeedBagAsync("BAG-RECOVERY");
        var request = new CloseDispatchRequest(
            Guid.NewGuid(), Guid.NewGuid(), "device-1", "123", ["BAG-RECOVERY"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.InvokeAsync<IDispatchClosingService, CloseDispatchResponse>(
                service => service.CloseAsync(request)));
        var recovered = await host.InvokeAsync<IDispatchClosingService, CloseDispatchResponse>(
            service => service.CloseAsync(request));

        Assert.Equal(1, recovered.Status);
        Assert.Equal("DSP-RECOVERED", recovered.DispatchId);
        Assert.Equal(1, postal.Calls);
    }

    private static CloseBagRequest BagRequest(Guid operationId) => new(
        Guid.NewGuid(), operationId, "device-1", "123", null, null, 999, null, null, "SEAL-1", "Road");

    private sealed class CountingBagClient : IPostalBagClient
    {
        internal int Calls { get; private set; }

        public Task<PostalBagResult> CloseAsync(
            PostalBagCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new PostalBagResult(
                PostalBagStatus.Completed,
                "BAG-RECOVERED",
                [],
                null,
                null));
        }
    }

    private sealed class FailOnceLabelGenerator : IBagLabelGenerator
    {
        internal int Calls { get; private set; }

        public string Generate(
            string bagBarcode,
            string deviceId,
            string destinationCode,
            BagCounts counts,
            DateTimeOffset createdAt)
        {
            Calls++;
            if (Calls == 1) throw new InvalidOperationException("Injected label failure.");
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(bagBarcode));
        }
    }

    private sealed class CountingDispatchClient : IPostalDispatchClient
    {
        internal int Calls { get; private set; }

        public Task<PostalDispatchResult> CloseAsync(
            PostalDispatchCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new PostalDispatchResult(true, "DSP-RECOVERED", null, null));
        }
    }

    private sealed class ThrowOnceOutboxWriter : IOutboxWriter
    {
        private int _calls;

        public void Enqueue(OutboxMessage message)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("Injected local finalization failure.");
        }
    }
}
