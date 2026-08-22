using Post.Edge.Application.Contracts;
using Post.Edge.Application.Services;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Integrations.Mock;
using Post.Edge.Infrastructure.Workers;

namespace Post.Edge.Application.Tests;

public sealed class InboundAndDestinationServiceTests
{
    private const string Barcode24 = "123450000000005954400001";

    [Fact]
    public async Task InboundRegistersTwentyFourDigitAndReplaysIdempotentResponse()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        var operationId = Guid.NewGuid();
        var request = Request([Barcode24], operationId);

        var first = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x => x.RegisterAsync(request));
        var second = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x => x.RegisterAsync(request));
        var conflict = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(request with { Barcodes = ["123450000000005954400002"] }));

        Assert.Equal(InboundResultStatus.Registered, first.Status);
        Assert.Equal("59544", first.DestinationCode);
        Assert.Equal(operationId, first.OperationId);
        Assert.Equal(first, second);
        Assert.Equal(InboundResultStatus.Error, conflict.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.ErrorCode);
    }

    [Fact]
    public async Task InboundCanonicalizesThirtySevenCharacterAlias()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();

        var response = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(Request([Barcode24 + "ABCDEFGHIJKLM"], Guid.NewGuid())));

        Assert.Equal(Barcode24, response.CanonicalBarcode);
        Assert.Equal(InboundResultStatus.Registered, response.Status);
    }

    [Fact]
    public async Task InboundResolvesAndPadsFourteenDigitDestination()
    {
        const string barcode14 = "12345678901234";
        await using var host = await EdgeApplicationTestHost.CreateAsync(new Dictionary<string, string?>
        {
            [$"Edge:Mock:FourteenDigitDestinations:{barcode14}"] = "123"
        });
        await host.InvokeAsync<IConfigurationSyncService>(service => service.SyncAsync());

        var response = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(Request([barcode14], Guid.NewGuid())));

        Assert.Equal(InboundResultStatus.Registered, response.Status);
        Assert.Equal("00123", response.DestinationCode);
    }

    [Fact]
    public async Task InboundRejectsMixedFourteenDigitRead()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();

        var response = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(Request(["12345678901234", Barcode24], Guid.NewGuid())));

        Assert.Equal(InboundResultStatus.Error, response.Status);
        Assert.Equal("INVALID_PARCEL", response.ErrorCode);
    }

    [Theory]
    [InlineData("990000000000005954400001", InboundResultStatus.Returned)]
    [InlineData("980000000000005954400001", InboundResultStatus.Refused)]
    public async Task InboundMapsCoreDisposition(string barcode, InboundResultStatus expected)
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();

        var response = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(Request([barcode], Guid.NewGuid())));

        Assert.Equal(expected, response.Status);
    }

    [Fact]
    public async Task PendingInboundIsCompletedByRetryWorker()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(new Dictionary<string, string?>
        {
            ["Edge:Mock:InboundPendingBarcodes:0"] = Barcode24
        });
        await host.InvokeAsync<IConfigurationSyncService>(service => service.SyncAsync());
        var request = Request([Barcode24], Guid.NewGuid());
        var response = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(request));

        var work = await host.InvokeAsync<IPendingWorkService, PendingWorkResult>(x => x.ProcessInboundAsync(10));
        var replay = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(request));

        Assert.Equal(InboundResultStatus.Pending, response.Status);
        Assert.Equal(1, work.Completed);
        Assert.Equal(InboundResultStatus.Registered, replay.Status);
    }

    [Fact]
    public async Task PendingInboundPermanentFailureReplacesCachedPendingResponse()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IPostalInboundClient>();
            services.AddSingleton<IPostalInboundClient, PendingThenFailedInboundClient>();
        });
        var request = Request([Barcode24], Guid.NewGuid());

        var pending = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(service =>
            service.RegisterAsync(request));
        var work = await host.InvokeAsync<IPendingWorkService, PendingWorkResult>(service =>
            service.ProcessInboundAsync(10));
        var replay = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(service =>
            service.RegisterAsync(request));

        Assert.Equal(InboundResultStatus.Pending, pending.Status);
        Assert.Equal(1, work.DeadLettered);
        Assert.Equal(InboundResultStatus.Error, replay.Status);
        Assert.Equal("POSTAL_REJECTED", replay.ErrorCode);
        Assert.Equal("Postal rejected pending inbound registration.", replay.ErrorMessage);
    }

    [Fact]
    public async Task PendingInboundSchedulesExponentialRetryWhenPostalResultIsStillPending()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IPostalInboundClient>();
            services.AddSingleton<IPostalInboundClient, AlwaysPendingInboundClient>();
        });
        var request = Request([Barcode24], Guid.NewGuid());
        var pending = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(service =>
            service.RegisterAsync(request));

        var work = await host.InvokeAsync<IPendingWorkService, PendingWorkResult>(service =>
            service.ProcessInboundAsync(10));
        var scheduled = await host.InvokeAsync<IPendingInboundRepository, PendingInboundItem?>(repository =>
            repository.FindByOperationIdAsync(
                request.OperationId!.Value,
                TestContext.Current.CancellationToken));

        Assert.Equal(InboundResultStatus.Pending, pending.Status);
        Assert.Equal(1, work.Retried);
        Assert.NotNull(scheduled);
        Assert.Equal(1, scheduled.Attempts);
        Assert.True(scheduled.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task DestinationAssignmentUsesStatusOneAndTwoAndIsIdempotent()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        var inbound = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(Request([Barcode24], Guid.NewGuid())));
        var operationId = Guid.NewGuid();
        var request = new DestinationAssignRequest(
            Guid.NewGuid(), operationId, "device-1", inbound.ReadId!.Value, "123", "C1");

        var success = await host.InvokeAsync<IDestinationAssignmentService, DestinationAssignResponse>(x => x.AssignAsync(request));
        var replay = await host.InvokeAsync<IDestinationAssignmentService, DestinationAssignResponse>(x => x.AssignAsync(request));
        var conflict = await host.InvokeAsync<IDestinationAssignmentService, DestinationAssignResponse>(x =>
            x.AssignAsync(request with { DestinationCode = "999" }));
        var missing = await host.InvokeAsync<IDestinationAssignmentService, DestinationAssignResponse>(x =>
            x.AssignAsync(request with { OperationId = Guid.NewGuid(), ReadId = Guid.NewGuid() }));

        Assert.Equal(1, success.Status);
        Assert.Equal("00123", success.DestinationCode);
        Assert.Equal(success, replay);
        Assert.Equal(2, conflict.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.ErrorCode);
        Assert.Equal(2, missing.Status);
    }

    [Fact]
    public async Task DestinationAssignmentCanResolveParcelByCanonicalBarcode()
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(Request([Barcode24], Guid.NewGuid())));

        var response = await host.InvokeAsync<IDestinationAssignmentService, DestinationAssignResponse>(x =>
            x.AssignAsync(new DestinationAssignRequest(
                Guid.NewGuid(), Guid.NewGuid(), "device-1", null, "123", "C1", Barcode24)));

        Assert.Equal(1, response.Status);
        Assert.NotEqual(Guid.Empty, response.ReadId);
    }

    [Fact]
    public async Task InboundValidatesPersistsAndLazilyUploadsImagesWithoutOutboxContent()
    {
        using var document = JsonDocument.Parse("{\"appearanceStatus\":\"intact\"}");
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        var content = new byte[] { 1, 2, 3, 4 };
        var request = Request([Barcode24], Guid.NewGuid()) with
        {
            SupplementaryData = document.RootElement.Clone(),
            Images = [new InboundImageDto("img-1", Convert.ToBase64String(content), "image/jpeg", "front")],
            TimeoutMs = 3_000
        };

        var response = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x => x.RegisterAsync(request));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<IInboundEvidenceRepository>()
                .FindImageAsync(response.ReadId!.Value, "img-1", TestContext.Current.CancellationToken);
            Assert.Equal(content, stored!.Content);
            var result = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(10, TestContext.Current.CancellationToken);
            Assert.Equal(4, result.Completed);
        }
        var uploader = Assert.IsType<MockCoreImageUploadClient>(host.Services.GetRequiredService<ICoreImageUploadClient>());
        Assert.Equal(2, uploader.Uploads.Count);
        Assert.Equal(content, uploader.Uploads.Single(upload => upload.AttachmentType == "parcel-image").Content);
        Assert.Equal("application/json",
            uploader.Uploads.Single(upload => upload.AttachmentType == "supplementary-data").MimeType);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("")]
    public async Task InboundRejectsInvalidImageContent(string content)
    {
        await using var host = await EdgeApplicationTestHost.CreateAsync();
        var response = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(x =>
            x.RegisterAsync(Request([Barcode24], Guid.NewGuid()) with
            {
                Images = [new InboundImageDto("img-1", content)]
            }));

        Assert.Equal(InboundResultStatus.Error, response.Status);
        Assert.Equal("INVALID_PARCEL", response.ErrorCode);
    }

    [Theory]
    [InlineData(Barcode24, false)]
    [InlineData("12345678901234", true)]
    public async Task InboundCoreOutagePersistsAndReconcilesWithoutDataLoss(
        string barcode,
        bool requiresDestinationLookup)
    {
        var flaky = new FlakyCoreApiClient();
        var settings = requiresDestinationLookup
            ? new Dictionary<string, string?>
            {
                [$"Edge:Mock:FourteenDigitDestinations:{barcode}"] = "123"
            }
            : null;
        await using var host = await EdgeApplicationTestHost.CreateAsync(settings, services =>
        {
            services.RemoveAll<ICoreApiClient>();
            services.AddSingleton<ICoreApiClient>(flaky);
        });
        if (requiresDestinationLookup)
            await host.InvokeAsync<IConfigurationSyncService>(service => service.SyncAsync());
        using var document = JsonDocument.Parse("{\"condition\":\"intact\"}");
        var request = Request([barcode], Guid.NewGuid()) with
        {
            SupplementaryData = document.RootElement.Clone(),
            Images = [new InboundImageDto("offline-image", "AQID", "image/jpeg")]
        };

        var pending = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(
            service => service.RegisterAsync(request));
        var replay = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(
            service => service.RegisterAsync(request));
        Assert.Equal(InboundResultStatus.Pending, pending.Status);
        Assert.Equal(pending, replay);

        await host.InvokeAsync<OutboxProcessor, OutboxProcessingResult>(processor => processor.ProcessBatchAsync(20));
        await host.InvokeAsync<OutboxProcessor, OutboxProcessingResult>(processor => processor.ProcessBatchAsync(20));

        var completed = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(
            service => service.RegisterAsync(request));
        Assert.Equal(InboundResultStatus.Registered, completed.Status);
        Assert.Equal(2, flaky.LookupCalls);
        Assert.Contains(flaky.Published, message =>
            message.MessageType == Post.Contracts.ContractMessageTypes.EdgeParcelReadingSubmitted);
        var uploader = Assert.IsType<MockCoreImageUploadClient>(
            host.Services.GetRequiredService<ICoreImageUploadClient>());
        Assert.Equal(2, uploader.Uploads.Count);
    }

    [Fact]
    public async Task InboundPropagatesCallerCancellationFromCoreLookup()
    {
        using var source = new CancellationTokenSource();
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<ICoreApiClient>();
            services.AddSingleton<ICoreApiClient>(new CancelingCoreApiClient(source));
        });
        var request = Request([Barcode24], Guid.NewGuid());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(
                service => service.RegisterAsync(request, source.Token)));
    }

    [Fact]
    public async Task InboundResumesDurableReceiptAfterLocalCrashWithoutRepeatingPostalInsert()
    {
        var postal = new CountingInboundClient();
        var outbox = new ThrowOnceOutboxWriter();
        await using var host = await EdgeApplicationTestHost.CreateAsync(customize: services =>
        {
            services.RemoveAll<IPostalInboundClient>();
            services.AddSingleton<IPostalInboundClient>(postal);
            services.RemoveAll<IOutboxWriter>();
            services.AddSingleton<IOutboxWriter>(outbox);
        });
        var request = Request([Barcode24], Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(
                service => service.RegisterAsync(request)));
        var recovered = await host.InvokeAsync<IInboundRegistrationService, InboundParcelResponse>(
            service => service.RegisterAsync(request));
        var saga = await host.InvokeAsync<IExternalOperationSagaRepository, ExternalOperationSaga?>(repository =>
            repository.GetAsync(request.OperationId!.Value, "parcel.inbound", default));

        Assert.Equal(InboundResultStatus.Registered, recovered.Status);
        Assert.Equal(1, postal.RegisterCalls);
        Assert.NotNull(saga);
        Assert.True(saga.Finalized);
    }

    private static InboundParcelRequest Request(IReadOnlyCollection<string> barcodes, Guid operationId) => new(
        Guid.NewGuid(), operationId, "device-1", barcodes, DateTimeOffset.UtcNow,
        100, 10, 20, 30, "Parcel", "Express");

    private sealed class FlakyCoreApiClient : ICoreApiClient
    {
        private readonly List<DeviceEvent> _published = [];
        internal int LookupCalls { get; private set; }
        internal IReadOnlyCollection<DeviceEvent> Published => _published;

        public Task<CoreInboundLookupResult> LookupInboundAsync(
            CoreInboundLookupRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LookupCalls++;
            if (LookupCalls == 1) throw new HttpRequestException("Core is temporarily unavailable.");
            return Task.FromResult(new CoreInboundLookupResult(
                CoreParcelDisposition.Normal,
                null,
                request.DestinationCenterCode,
                Guid.NewGuid()));
        }

        public Task PublishAsync(
            string messageType,
            string payloadJson,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _published.Add(new DeviceEvent(messageType, payloadJson, idempotencyKey));
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysPendingInboundClient : IPostalInboundClient
    {
        public Task<PostalInboundResult> RegisterAsync(
            PostalInboundCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PostalInboundResult(
                PostalInboundStatus.Pending,
                command.OperationId.ToString("N"),
                null,
                null));
        }

        public Task<PostalInboundResult> GetPendingResultAsync(
            string trackingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PostalInboundResult(
                PostalInboundStatus.Pending,
                trackingId,
                null,
                "Postal work remains pending."));
        }
    }

    private sealed class CancelingCoreApiClient(CancellationTokenSource source) : ICoreApiClient
    {
        public Task<CoreInboundLookupResult> LookupInboundAsync(
            CoreInboundLookupRequest request,
            CancellationToken cancellationToken)
        {
            source.Cancel();
            return Task.FromCanceled<CoreInboundLookupResult>(cancellationToken);
        }

        public Task PublishAsync(
            string messageType,
            string payloadJson,
            string idempotencyKey,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PendingThenFailedInboundClient : IPostalInboundClient
    {
        public Task<PostalInboundResult> RegisterAsync(
            PostalInboundCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PostalInboundResult(
                PostalInboundStatus.Pending,
                "pending-tracking",
                null,
                null));
        }

        public Task<PostalInboundResult> GetPendingResultAsync(
            string trackingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PostalInboundResult(
                PostalInboundStatus.Failed,
                trackingId,
                "POSTAL_REJECTED",
                "Postal rejected pending inbound registration."));
        }
    }

    private sealed class CountingInboundClient : IPostalInboundClient
    {
        internal int RegisterCalls { get; private set; }

        public Task<PostalInboundResult> RegisterAsync(
            PostalInboundCommand command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegisterCalls++;
            return Task.FromResult(new PostalInboundResult(
                PostalInboundStatus.Registered,
                command.OperationId.ToString("N"),
                null,
                null));
        }

        public Task<PostalInboundResult> GetPendingResultAsync(
            string trackingId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PostalInboundResult(
                PostalInboundStatus.Registered,
                trackingId,
                null,
                null));
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
