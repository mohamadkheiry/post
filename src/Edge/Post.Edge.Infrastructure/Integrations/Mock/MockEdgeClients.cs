using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.ValueObjects;
using Post.Edge.Domain.Devices;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Integrations.Mock;

public sealed class MockDestinationLookupClient(IOptions<EdgeMockOptions> options) : IDestinationLookupClient
{
    public Task<string?> ResolveAsync(string fourteenDigitBarcode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(options.Value.FourteenDigitDestinations.TryGetValue(fourteenDigitBarcode, out var destination)
            ? destination.PadLeft(5, '0')
            : null);
    }
}

public sealed class MockCoreApiClient : ICoreApiClient, ICoreEdgeHealthClient
{
    private readonly ConcurrentQueue<DeviceEvent> _published = new();
    public IReadOnlyCollection<DeviceEvent> Published => _published.ToArray();

    public Task<CoreInboundLookupResult> LookupInboundAsync(
        CoreInboundLookupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalBarcode = request.Barcodes.First();
        var disposition = canonicalBarcode.StartsWith("99", StringComparison.Ordinal)
            ? CoreParcelDisposition.Returned
            : canonicalBarcode.StartsWith("98", StringComparison.Ordinal)
                ? CoreParcelDisposition.Refused
                : CoreParcelDisposition.Normal;
        return Task.FromResult(new CoreInboundLookupResult(disposition, null, null, Guid.NewGuid()));
    }

    public Task PublishAsync(string messageType, string payloadJson, string idempotencyKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _published.Enqueue(new DeviceEvent(messageType, payloadJson, idempotencyKey));
        return Task.CompletedTask;
    }

    public Task RegisterAsync(CoreEdgeRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task SendHeartbeatAsync(CoreEdgeHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class MockPostalInboundClient(IOptions<EdgeMockOptions> options) : IPostalInboundClient
{
    private readonly ConcurrentDictionary<string, int> _pendingPolls = new(StringComparer.Ordinal);

    public Task<PostalInboundResult> RegisterAsync(PostalInboundCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var barcode = command.Barcodes.CanonicalBarcode.Canonical;
        if (options.Value.InboundFailureBarcodes.Contains(barcode, StringComparer.Ordinal))
            return Task.FromResult(new PostalInboundResult(PostalInboundStatus.Failed, null, "MOCK_REJECTED", "Mock postal rejection."));
        if (options.Value.InboundPendingBarcodes.Contains(barcode, StringComparer.Ordinal))
        {
            var tracking = command.OperationId.ToString("N");
            _pendingPolls.TryAdd(tracking, 0);
            return Task.FromResult(new PostalInboundResult(PostalInboundStatus.Pending, tracking, null, null));
        }
        return Task.FromResult(new PostalInboundResult(PostalInboundStatus.Registered, command.OperationId.ToString("N"), null, null));
    }

    public Task<PostalInboundResult> GetPendingResultAsync(string trackingId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var polls = _pendingPolls.AddOrUpdate(trackingId, 1, (_, count) => count + 1);
        return Task.FromResult(polls >= 1
            ? new PostalInboundResult(PostalInboundStatus.Registered, trackingId, null, null)
            : new PostalInboundResult(PostalInboundStatus.Pending, trackingId, null, null));
    }
}

public sealed class MockPostalBagClient(IOptions<EdgeMockOptions> options) : IPostalBagClient
{
    public Task<PostalBagResult> CloseAsync(PostalBagCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Value.BagServiceUnavailable)
            return Task.FromResult(new PostalBagResult(PostalBagStatus.Unavailable, null, [], "MOCK_UNAVAILABLE", "Mock bag service is unavailable."));
        var failures = command.Parcels
            .Where(x => options.Value.BagFailureBarcodes.Contains(x.CanonicalBarcode, StringComparer.Ordinal))
            .Select(x => new PostalParcelFailure(x.ReadId, "MOCK_REJECTED", "Mock parcel rejection.", "Mock"))
            .ToArray();
        return Task.FromResult(new PostalBagResult(
            PostalBagStatus.Completed,
            $"BAG-{command.OperationId:N}".ToUpperInvariant(),
            failures,
            null,
            null));
    }
}

public sealed class MockPostalDispatchClient(IOptions<EdgeMockOptions> options) : IPostalDispatchClient
{
    public Task<PostalDispatchResult> CloseAsync(PostalDispatchCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(options.Value.DispatchServiceUnavailable
            ? new PostalDispatchResult(false, null, "MOCK_UNAVAILABLE", "Mock dispatch service is unavailable.")
            : new PostalDispatchResult(true, $"DSP-{command.OperationId:N}".ToUpperInvariant(), null, null));
    }
}

public sealed class MockConfigurationSource(
    IOptions<EdgeMockOptions> options,
    IOptions<EdgeAutoDispatchOptions> autoDispatch) : IConfigurationSource
{
    public Task<EdgeConfigurationDto?> FetchAsync(long? currentVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        var devices = settings.Devices.Count == 0 ? [new MockDeviceOptions()] : settings.Devices;
        var configuredDevices = devices.Select(x =>
        {
            var verifier = DeviceSecretVerifier.DeriveForDevelopment(x.Token);
            return new DeviceConfigurationDto(
                x.DeviceId,
                x.LogicalId,
                x.IsActive,
                verifier.Algorithm,
                verifier.Iterations,
                verifier.SaltBase64,
                verifier.HashBase64);
        }).ToArray();
        return Task.FromResult<EdgeConfigurationDto?>(new EdgeConfigurationDto(
            settings.CenterCode,
            settings.ConfigurationVersion,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromHours(24),
            configuredDevices,
            settings.FourteenDigitDestinations.Values.Append(settings.CenterCode).Distinct().ToArray(),
            autoDispatch.Value.Enabled,
            checked((int)Math.Ceiling(autoDispatch.Value.MaximumBagAge.TotalMinutes))));
    }
}

public sealed class BasicBagLabelGenerator : IBagLabelGenerator
{
    public string Generate(
        string bagBarcode,
        string deviceId,
        string destinationCode,
        BagCounts counts,
        DateTimeOffset createdAt)
    {
        var label = $"BAG={bagBarcode}\nDEVICE={deviceId}\nDEST={destinationCode}\nN={counts.N};P={counts.P};M={counts.M};Q={counts.Q}\nUTC={createdAt:O}";
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(label));
    }
}

public sealed class InMemoryDeviceEventPublisher : IDeviceEventPublisher
{
    private readonly ConcurrentQueue<DeviceEvent> _messages = new();
    public IReadOnlyCollection<DeviceEvent> Messages => _messages.ToArray();

    public Task PublishAsync(DeviceEvent message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }
}

public sealed class MockParcelInfoClient : IParcelInfoClient
{
    public Task<ParcelInfoResult?> GetAsync(
        string centerCode,
        string parcelBarcode,
        int userId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ParcelInfoResult?>(new ParcelInfoResult(100, "MOCK", 0, false));
    }
}

public sealed class MockCoreImageUploadClient : ICoreImageUploadClient
{
    private readonly ConcurrentQueue<CoreImageUpload> _uploads = new();
    public IReadOnlyCollection<CoreImageUpload> Uploads => _uploads.ToArray();

    public Task UploadAsync(CoreImageUpload image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _uploads.Enqueue(image with { Content = image.Content.ToArray() });
        return Task.CompletedTask;
    }
}
