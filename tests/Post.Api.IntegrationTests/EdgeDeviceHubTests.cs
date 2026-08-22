using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Console;
using Post.Contracts;
using Post.Contracts.V1.Device;
using Post.DeviceSimulator;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Services;

namespace Post.Api.IntegrationTests;

public sealed class EdgeDeviceHubTests
{
    private const string DeviceId = "device-1";
    private const string DeviceToken = "testing-device-token";
    private const string BarcodeOne = "123450000000005954400001";
    private const string BarcodeTwo = "123450000000005954400002";
    private const string BarcodeThree = "123450000000005954400003";
    private static readonly string[] LoopbackAddresses = ["127.0.0.1"];
    private static readonly string LocalAdministratorPassword =
        string.Concat("edge-local-", "administrator-", "test-value!");
    private static readonly string RotatedLocalAdministratorPassword =
        string.Concat("rotated-edge-", "administrator-", "test-value!");

    [Fact]
    public async Task EdgeRoutesExposeWebSocketOnlyHubStatusAndReadiness()
    {
        await using var factory = new EdgeApiFactory();
        using var client = factory.CreateClient();

        using var negotiate = await client.PostAsync(new Uri("/ws/device/negotiate?negotiateVersion=1", UriKind.Relative), null, TestContext.Current.CancellationToken);
        using var negotiateJson = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var plainHttp = await client.GetAsync(new Uri("/ws/device", UriKind.Relative), TestContext.Current.CancellationToken);
        using var status = await client.GetAsync(new Uri("/api/edge/status", UriKind.Relative), TestContext.Current.CancellationToken);
        using var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
        var transport = Assert.Single(negotiateJson.RootElement.GetProperty("availableTransports").EnumerateArray());
        Assert.Equal("WebSockets", transport.GetProperty("transport").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, plainHttp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var statusPayload = JsonDocument.Parse(await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(statusPayload.RootElement.GetProperty("availableDiskBytes").GetInt64() >= 0);
        var hub = factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(10), hub.KeepAliveInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), hub.ClientTimeoutInterval);
        Assert.Equal(192L * 1024 * 1024, hub.MaximumReceiveMessageSize);
        var formatter = factory.Services.GetRequiredService<IOptions<JsonConsoleFormatterOptions>>().Value;
        Assert.True(formatter.IncludeScopes);
        Assert.True(formatter.UseUtcTimestamp);
        Assert.Equal("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", formatter.TimestampFormat);
    }

    [Fact]
    public async Task AuthFirstFullWorkflowAndIdempotencyAreEnforcedOnWire()
    {
        await using var factory = new EdgeApiFactory();
        await using var connection = factory.CreateHubConnection();
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var unauthenticated = await connection.InvokeAsync<DeviceEnvelopeV1<DeviceProtocolErrorV1>>(DeviceHubMethodsV1.RegisterInbound, Request(ContractMessageTypes.DeviceParcelRead,
                new RegisterInboundRequestV1([BarcodeOne], DateTimeOffset.UnixEpoch)), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("VALIDATION_ERROR", unauthenticated.Payload.Code);

        var auth = await AuthenticateAsync(connection);
        var authReplay = await AuthenticateAsync(connection);
        Assert.Equal(DeviceStatusCodeV1.Success, auth.Payload.Status);
        Assert.Equal(auth.Payload.SessionId, authReplay.Payload.SessionId);
        Assert.NotNull(auth.Payload.SessionId);
        Assert.NotNull(auth.Payload.EdgeServerTime);

        var inboundOperation = Guid.NewGuid();
        var inboundPayload = new RegisterInboundRequestV1(
            [BarcodeOne],
            DateTimeOffset.UnixEpoch,
            new PhysicalAttributesV1(100, new ParcelDimensionsV1(10, 20, 30)),
            "Parcel");
        var inbound = await connection.InvokeAsync<DeviceEnvelopeV1<RegisterInboundResponseV1>>(DeviceHubMethodsV1.RegisterInbound, Request(ContractMessageTypes.DeviceParcelRead, inboundPayload, inboundOperation), cancellationToken: TestContext.Current.CancellationToken);
        var replay = await connection.InvokeAsync<DeviceEnvelopeV1<RegisterInboundResponseV1>>(DeviceHubMethodsV1.RegisterInbound, Request(ContractMessageTypes.DeviceParcelRead, inboundPayload, inboundOperation), cancellationToken: TestContext.Current.CancellationToken);
        var conflict = await connection.InvokeAsync<DeviceEnvelopeV1<RegisterInboundResponseV1>>(DeviceHubMethodsV1.RegisterInbound, Request(ContractMessageTypes.DeviceParcelRead,
                inboundPayload with { Barcodes = [BarcodeTwo] }, inboundOperation), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DeviceStatusCodeV1.Success, inbound.Payload.Status);
        Assert.Equal(inbound.Payload.Status, replay.Payload.Status);
        Assert.Equal(DeviceStatusCodeV1.Error, conflict.Payload.Status);
        Assert.False(inbound.Payload.OperationIdIsBestEffort);

        var fallbackCorrelation = Guid.NewGuid();
        var fallback = await connection.InvokeAsync<DeviceEnvelopeV1<RegisterInboundResponseV1>>(DeviceHubMethodsV1.RegisterInbound, Request(ContractMessageTypes.DeviceParcelRead,
                new RegisterInboundRequestV1([BarcodeTwo], DateTimeOffset.UnixEpoch),
                correlationId: fallbackCorrelation), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(fallbackCorrelation, fallback.OperationId);
        Assert.True(fallback.Payload.OperationIdIsBestEffort);

        var destinationRequest = Request(ContractMessageTypes.DeviceDestinationAssign,
            new AssignDestinationRequestV1(BarcodeOne, "59544", "CHUTE-1"), Guid.NewGuid());
        var destination = await connection.InvokeAsync<DeviceEnvelopeV1<AssignDestinationResponseV1>>(DeviceHubMethodsV1.AssignDestination, destinationRequest, cancellationToken: TestContext.Current.CancellationToken);
        var destinationReplay = await connection.InvokeAsync<DeviceEnvelopeV1<AssignDestinationResponseV1>>(DeviceHubMethodsV1.AssignDestination, destinationRequest, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DestinationAssignmentStatusCodeV1.Confirmed, destination.Payload.Status);
        Assert.Equal(destination.Payload, destinationReplay.Payload);

        var bagRequest = Request(ContractMessageTypes.DeviceBagClose,
            new CloseBagRequestV1("59544", "SEAL-1", "Road", Count: 999), Guid.NewGuid());
        var bag = await connection.InvokeAsync<DeviceEnvelopeV1<CloseBagResponseV1>>(DeviceHubMethodsV1.CloseBag, bagRequest, cancellationToken: TestContext.Current.CancellationToken);
        var bagReplay = await connection.InvokeAsync<DeviceEnvelopeV1<CloseBagResponseV1>>(DeviceHubMethodsV1.CloseBag, bagRequest, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DeviceStatusCodeV1.Success, bag.Payload.Status);
        Assert.Equal(BagCloseResultTypeV1.Completed, bag.Payload.ResultType);
        Assert.Equal(1, bag.Payload.Counts?.EffectiveCount);
        Assert.Equal(bag.Payload.Status, bagReplay.Payload.Status);
        Assert.Equal(bag.Payload.ResultType, bagReplay.Payload.ResultType);
        Assert.Equal(bag.Payload.BagBarcode, bagReplay.Payload.BagBarcode);
        Assert.Equal(bag.Payload.BagLabel, bagReplay.Payload.BagLabel);
        Assert.Equal(bag.Payload.Counts, bagReplay.Payload.Counts);
        Assert.Equal(bag.Payload.Errors, bagReplay.Payload.Errors);

        var dispatchRequest = Request(ContractMessageTypes.DeviceDispatchClose,
            new CloseDispatchRequestV1("59544"), Guid.NewGuid());
        var dispatch = await connection.InvokeAsync<DeviceEnvelopeV1<CloseDispatchResponseV1>>(DeviceHubMethodsV1.CloseDispatch, dispatchRequest, cancellationToken: TestContext.Current.CancellationToken);
        var dispatchReplay = await connection.InvokeAsync<DeviceEnvelopeV1<CloseDispatchResponseV1>>(DeviceHubMethodsV1.CloseDispatch, dispatchRequest, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DeviceStatusCodeV1.Success, dispatch.Payload.Status);
        Assert.Equal(DispatchCloseResultTypeV1.Completed, dispatch.Payload.ResultType);
        Assert.Equal(1, dispatch.Payload.BagCount);
        Assert.Equal(1, dispatch.Payload.TotalParcels);
        Assert.Empty(dispatch.Payload.ProblemParcels ?? []);
        Assert.Equal(dispatch.Payload.Status, dispatchReplay.Payload.Status);
        Assert.Equal(dispatch.Payload.ResultType, dispatchReplay.Payload.ResultType);
        Assert.Equal(dispatch.Payload.DispatchId, dispatchReplay.Payload.DispatchId);
        Assert.Equal(dispatch.Payload.BagCount, dispatchReplay.Payload.BagCount);
        Assert.Equal(dispatch.Payload.TotalParcels, dispatchReplay.Payload.TotalParcels);
        Assert.Equal(dispatch.Payload.ProblemParcels, dispatchReplay.Payload.ProblemParcels);
    }

    [Fact]
    public async Task AuthenticationFailureOmitsSuccessFieldsClosesAndAllowsFreshReconnect()
    {
        await using var factory = new EdgeApiFactory();
        await using var rejected = factory.CreateHubConnection();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rejected.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await rejected.StartAsync(TestContext.Current.CancellationToken);

        var result = await rejected.InvokeAsync<JsonElement>(DeviceHubMethodsV1.Auth, Request(ContractMessageTypes.DeviceAuthenticate,
                new DeviceAuthenticateRequestV1(DeviceId, "wrong-token")), cancellationToken: TestContext.Current.CancellationToken);
        var payload = result.GetProperty("payload");

        Assert.Equal(2, payload.GetProperty("status").GetInt32());
        Assert.False(payload.TryGetProperty("sessionId", out _));
        Assert.False(payload.TryGetProperty("edgeServerTime", out _));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await using var fresh = factory.CreateHubConnection();
        await fresh.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceStatusCodeV1.Success, (await AuthenticateAsync(fresh)).Payload.Status);
    }

    [Fact]
    public async Task UnexpectedOperationFailureUsesCorrelationAsExplicitBestEffortOperationId()
    {
        await using var factory = new EdgeApiFactory(configureServices: services =>
        {
            services.RemoveAll<IDestinationAssignmentService>();
            services.AddScoped<IDestinationAssignmentService, ThrowingDestinationAssignmentService>();
        });
        await using var connection = factory.CreateHubConnection();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceStatusCodeV1.Success, (await AuthenticateAsync(connection)).Payload.Status);
        var correlationId = Guid.NewGuid();
        var request = Request(
            ContractMessageTypes.DeviceDestinationAssign,
            new AssignDestinationRequestV1(BarcodeOne, "59544", "CHUTE-1"),
            correlationId: correlationId);

        var response = await connection.InvokeAsync<DeviceEnvelopeV1<AssignDestinationResponseV1>>(
            DeviceHubMethodsV1.AssignDestination,
            request,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DestinationAssignmentStatusCodeV1.Error, response.Payload.Status);
        Assert.Equal(correlationId, response.OperationId);
        Assert.True(response.Payload.OperationIdIsBestEffort);
        Assert.Equal("Destination assignment failed.", response.Payload.ErrorMessage);
    }

    [Fact]
    public async Task OversizedAuthenticationInputFailsSafelyAndClosesConnection()
    {
        await using var factory = new EdgeApiFactory();
        await using var connection = factory.CreateHubConnection();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await connection.StartAsync(TestContext.Current.CancellationToken);
        var oversizedToken = new string(
            'T',
            DeviceAuthenticationLimits.MaximumDeviceTokenCharacters + 1);

        var response = await connection.InvokeAsync<DeviceEnvelopeV1<DeviceProtocolErrorV1>>(DeviceHubMethodsV1.Auth, Request(
                ContractMessageTypes.DeviceAuthenticate,
                new DeviceAuthenticateRequestV1(DeviceId, oversizedToken)), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("VALIDATION_ERROR", response.Payload.Code);
        Assert.DoesNotContain(oversizedToken, response.Payload.Message, StringComparison.Ordinal);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AuthenticationFailureOmitsConditionalFieldsEvenWhenApplicationReturnsThem()
    {
        await using var factory = new EdgeApiFactory(configureServices: services =>
        {
            services.RemoveAll<IDeviceConnectionService>();
            services.AddScoped<IDeviceConnectionService, HostileFailureDeviceConnectionService>();
        });
        await using var connection = factory.CreateHubConnection();
        await connection.StartAsync(TestContext.Current.CancellationToken);

        var result = await connection.InvokeAsync<JsonElement>(DeviceHubMethodsV1.Auth, Request(ContractMessageTypes.DeviceAuthenticate,
                new DeviceAuthenticateRequestV1(DeviceId, DeviceToken)), cancellationToken: TestContext.Current.CancellationToken);
        var payload = result.GetProperty("payload");

        Assert.Equal(2, payload.GetProperty("status").GetInt32());
        Assert.False(payload.TryGetProperty("sessionId", out _));
        Assert.False(payload.TryGetProperty("edgeServerTime", out _));
    }

    [Fact]
    public async Task AuthenticationDeadlineClosesIdleConnection()
    {
        await using var factory = new EdgeApiFactory(new Dictionary<string, string?>
        {
            ["Edge:DeviceTransport:AuthenticationDeadline"] = "00:00:01"
        });
        await using var connection = factory.CreateHubConnection();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await connection.StartAsync(TestContext.Current.CancellationToken);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnvelopeValidationUsesCanonicalProtocolCatalogAndUnknownTargetsStayTransportErrors()
    {
        await using var factory = new EdgeApiFactory();
        await using var rejected = factory.CreateHubConnection();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rejected.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };
        await rejected.StartAsync(TestContext.Current.CancellationToken);
        var wrongVersion = new DeviceEnvelopeV1<DeviceAuthenticateRequestV1>(
            "2.0",
            ContractMessageTypes.DeviceAuthenticate,
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            new DeviceAuthenticateRequestV1(DeviceId, DeviceToken));
        var versionError = await rejected.InvokeAsync<DeviceEnvelopeV1<DeviceProtocolErrorV1>>(DeviceHubMethodsV1.Auth, wrongVersion, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("UNSUPPORTED_PROTOCOL_VERSION", versionError.Payload.Code);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await using var connection = factory.CreateHubConnection();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await AuthenticateAsync(connection);
        var wrongType = new DeviceEnvelopeV1<AssignDestinationRequestV1>(
            ContractVersions.V1,
            ContractMessageTypes.DeviceBagClose,
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            new AssignDestinationRequestV1(BarcodeOne, "59544"));
        var typeError = await connection.InvokeAsync<DeviceEnvelopeV1<DeviceProtocolErrorV1>>(DeviceHubMethodsV1.AssignDestination, wrongType, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("UNKNOWN_MESSAGE_TYPE", typeError.Payload.Code);

        var emptyCorrelation = new DeviceEnvelopeV1<CloseDispatchRequestV1>(
            ContractVersions.V1,
            ContractMessageTypes.DeviceDispatchClose,
            Guid.Empty,
            DateTimeOffset.UnixEpoch,
            new CloseDispatchRequestV1("59544"));
        var validationError = await connection.InvokeAsync<DeviceEnvelopeV1<DeviceProtocolErrorV1>>(DeviceHubMethodsV1.CloseDispatch, emptyCorrelation, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("VALIDATION_ERROR", validationError.Payload.Code);
        await Assert.ThrowsAsync<HubException>(() => connection.InvokeAsync<object>("UnknownTarget", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImageSchemaBoundariesReturnCanonicalProtocolErrors()
    {
        await using var factory = new EdgeApiFactory();
        await using var connection = factory.CreateHubConnection();
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await AuthenticateAsync(connection);

        var thirtyThree = Enumerable.Range(0, 33)
            .Select(index => new ParcelInlineImageV1($"img-{index}", "AQID"))
            .ToArray();
        var countError = await InvokeInboundErrorAsync(connection, BarcodeOne, thirtyThree);
        var oversizeError = await InvokeInboundErrorAsync(connection, BarcodeOne,
            [new ParcelInlineImageV1("oversize", new string('A', 5_000_001))]);
        var malformedError = await InvokeInboundErrorAsync(connection, BarcodeOne,
            [new ParcelInlineImageV1("malformed", "not-base64")]);
        var nullItemError = await InvokeInboundErrorAsync(connection, BarcodeOne, [null!]);

        Assert.Equal("VALIDATION_ERROR", countError.Payload.Code);
        Assert.Equal("VALIDATION_ERROR", oversizeError.Payload.Code);
        Assert.Equal("VALIDATION_ERROR", malformedError.Payload.Code);
        Assert.Equal("MISSING_REQUIRED_FIELD", nullItemError.Payload.Code);

        var maximum = await connection.InvokeAsync<DeviceEnvelopeV1<RegisterInboundResponseV1>>(DeviceHubMethodsV1.RegisterInbound, Request(ContractMessageTypes.DeviceParcelRead,
                new RegisterInboundRequestV1(
                    [BarcodeTwo],
                    DateTimeOffset.UnixEpoch,
                    Images: [new ParcelInlineImageV1("maximum", new string('A', 5_000_000))]),
                Guid.NewGuid()), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DeviceStatusCodeV1.Success, maximum.Payload.Status);

        var thirtyTwo = Enumerable.Range(0, 32)
            .Select(index => new ParcelInlineImageV1($"valid-{index}", "AQID"))
            .ToArray();
        var accepted = await connection.InvokeAsync<DeviceEnvelopeV1<RegisterInboundResponseV1>>(DeviceHubMethodsV1.RegisterInbound, Request(ContractMessageTypes.DeviceParcelRead,
                new RegisterInboundRequestV1([BarcodeThree], DateTimeOffset.UnixEpoch, Images: thirtyTwo),
                Guid.NewGuid()), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(DeviceStatusCodeV1.Success, accepted.Payload.Status);
    }

    [Fact]
    public async Task IpBindingAdminAuditAndProductionAuthorizationAreEnforced()
    {
        await using var bindingFactory = new EdgeApiFactory(new Dictionary<string, string?>
        {
            ["Edge:Security:AllowUnboundDevices"] = "false"
        });
        using var bindingClient = bindingFactory.CreateClient();
        await using (var unboundConnection = bindingFactory.CreateHubConnection())
        {
            await unboundConnection.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(DeviceStatusCodeV1.Error, (await AuthenticateAsync(unboundConnection)).Payload.Status);
        }
        using var update = await bindingClient.PutAsJsonAsync(new Uri($"/admin/devices/{DeviceId}/ip", UriKind.Relative), new { ipAddresses = LoopbackAddresses }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        await using var boundConnection = bindingFactory.CreateHubConnection();
        await boundConnection.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceStatusCodeV1.Success, (await AuthenticateAsync(boundConnection)).Payload.Status);

        await using var securedFactory = new EdgeApiFactory(new Dictionary<string, string?>
        {
            ["Edge:LocalAdmin:AuthenticationEnabled"] = "true",
            ["Edge:LocalAdmin:InitialPassword"] = LocalAdministratorPassword
        });
        using var securedClient = securedFactory.CreateClient();
        using var unauthorized = await securedClient.PutAsJsonAsync(new Uri($"/admin/devices/{DeviceId}/ip", UriKind.Relative), new { ipAddresses = LoopbackAddresses }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var localAccessToken = await LoginLocalAdministratorAsync(
            securedClient,
            LocalAdministratorPassword);
        using var authorizedRequest = BindingRequest(localAccessToken);
        using var authorized = await securedClient.SendAsync(authorizedRequest, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        await using var database = new SqliteConnection($"Data Source={securedFactory.DatabasePath}");
        await database.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT PayloadJson FROM outbox WHERE MessageType = 'edge.device-ip-binding.changed' LIMIT 1";
        var auditPayload = Assert.IsType<string>(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        Assert.Contains("\"bindingCount\":1", auditPayload, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", auditPayload, StringComparison.Ordinal);

        using var unknown = await securedClient.PutAsJsonAsync(new Uri("/admin/devices/unknown/ip", UriKind.Relative), new { ipAddresses = LoopbackAddresses }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task ForwardedDeviceAddressIsAcceptedOnlyFromExplicitTrustedProxy()
    {
        const string forwardedDeviceAddress = "203.0.113.25";
        var trustedSettings = new Dictionary<string, string?>
        {
            ["Edge:Security:AllowUnboundDevices"] = "false",
            ["Edge:TrustedProxy:Enabled"] = "true",
            ["Edge:TrustedProxy:KnownProxies:0"] = "127.0.0.1"
        };
        var headers = new Dictionary<string, string>
        {
            ["X-Forwarded-For"] = forwardedDeviceAddress,
            ["X-Forwarded-Proto"] = "https"
        };
        IReadOnlyCollection<string> forwardedAddresses = [forwardedDeviceAddress];
        await using (var trustedFactory = new EdgeApiFactory(trustedSettings))
        {
            using var client = trustedFactory.CreateClient();
            using var binding = await client.PutAsJsonAsync(new Uri($"/admin/devices/{DeviceId}/ip", UriKind.Relative), new { ipAddresses = forwardedAddresses }, cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, binding.StatusCode);
            await using var connection = trustedFactory.CreateHubConnection(headers);
            await connection.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(DeviceStatusCodeV1.Success, (await AuthenticateAsync(connection)).Payload.Status);
        }

        trustedSettings["Edge:TrustedProxy:KnownProxies:0"] = "10.0.0.10";
        await using var untrustedFactory = new EdgeApiFactory(trustedSettings);
        using (var client = untrustedFactory.CreateClient())
        using (var binding = await client.PutAsJsonAsync(new Uri($"/admin/devices/{DeviceId}/ip", UriKind.Relative), new { ipAddresses = forwardedAddresses }, cancellationToken: TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, binding.StatusCode);
        }
        await using var untrustedConnection = untrustedFactory.CreateHubConnection(headers);
        await untrustedConnection.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DeviceStatusCodeV1.Error, (await AuthenticateAsync(untrustedConnection)).Payload.Status);
    }

    [Fact]
    public async Task EdgeAdministrationUsesLocalSessionAndRejectsCentralJwt()
    {
        await using var factory = new EdgeApiFactory(new Dictionary<string, string?>
        {
            ["Edge:LocalAdmin:AuthenticationEnabled"] = "true",
            ["Edge:LocalAdmin:InitialPassword"] = LocalAdministratorPassword
        });
        using var client = factory.CreateClient();

        using var anonymousBinding = await client.PutAsJsonAsync(new Uri($"/admin/devices/{DeviceId}/ip", UriKind.Relative), new { ipAddresses = LoopbackAddresses }, cancellationToken: TestContext.Current.CancellationToken);
        using var centralJwtRequest = BindingRequest(CoreApiTestClient.AdministratorToken());
        using var centralJwtResponse = await client.SendAsync(centralJwtRequest, TestContext.Current.CancellationToken);
        using var anonymousStatus = await client.GetAsync(new Uri("/api/edge/status", UriKind.Relative), TestContext.Current.CancellationToken);
        using var anonymousBackup = await client.PostAsync(new Uri("/admin/storage/backup", UriKind.Relative), null, TestContext.Current.CancellationToken);
        using var centralBackupRequest = BackupRequest(CoreApiTestClient.AdministratorToken());
        using var centralBackup = await client.SendAsync(centralBackupRequest, TestContext.Current.CancellationToken);
        var localAccessToken = await LoginLocalAdministratorAsync(client, LocalAdministratorPassword);
        using var localBindingRequest = BindingRequest(localAccessToken);
        using var localBindingResponse = await client.SendAsync(localBindingRequest, TestContext.Current.CancellationToken);
        using var localStatusRequest = StatusRequest(localAccessToken);
        using var localStatusResponse = await client.SendAsync(localStatusRequest, TestContext.Current.CancellationToken);
        using var localBackupRequest = BackupRequest(localAccessToken);
        using var localBackupResponse = await client.SendAsync(localBackupRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousBinding.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, centralJwtResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousStatus.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousBackup.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, centralBackup.StatusCode);
        Assert.Equal(HttpStatusCode.OK, localBindingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, localStatusResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, localBackupResponse.StatusCode);
    }

    [Fact]
    public async Task LocalAdministratorApiBoundsBodiesRotatesSessionsAndLogsOut()
    {
        await using var factory = new EdgeApiFactory(new Dictionary<string, string?>
        {
            ["Edge:LocalAdmin:AuthenticationEnabled"] = "true",
            ["Edge:LocalAdmin:InitialPassword"] = LocalAdministratorPassword,
            ["Edge:LocalAdmin:LoginPermitLimit"] = "20",
            ["Edge:LocalAdmin:MaximumRequestBodyBytes"] = "4096"
        });
        using var client = factory.CreateClient();

        using var nullLogin = await client.PostAsJsonAsync(new Uri("/admin/auth/login", UriKind.Relative), new { userName = (string?)null, password = (string?)null }, cancellationToken: TestContext.Current.CancellationToken);
        using var oversized = await client.PostAsJsonAsync(new Uri("/admin/auth/login", UriKind.Relative), new { userName = "admin", password = new string('X', 5_000) }, cancellationToken: TestContext.Current.CancellationToken);
        using var chunkedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/admin/auth/login", UriKind.Relative))
        {
            Content = JsonContent.Create(new { userName = "admin", password = new string('X', 5_000) })
        };
        chunkedRequest.Headers.TransferEncodingChunked = true;
        chunkedRequest.Content.Headers.ContentLength = null;
        using var chunkedOversized = await client.SendAsync(
            chunkedRequest,
            TestContext.Current.CancellationToken);
        using var unknown = await client.PostAsJsonAsync(new Uri("/admin/auth/login", UriKind.Relative), new { userName = "unknown", password = LocalAdministratorPassword }, cancellationToken: TestContext.Current.CancellationToken);
        using var wrong = await client.PostAsJsonAsync(new Uri("/admin/auth/login", UriKind.Relative), new { userName = "admin", password = RotatedLocalAdministratorPassword }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, nullLogin.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunkedOversized.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(
            await unknown.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await wrong.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var oldToken = await LoginLocalAdministratorAsync(client, LocalAdministratorPassword);
        using var nullRotation = PasswordRotationRequest(oldToken, null, null, null);
        using var nullRotationResponse = await client.SendAsync(nullRotation, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, nullRotationResponse.StatusCode);
        using var rotation = PasswordRotationRequest(
            oldToken,
            "admin",
            LocalAdministratorPassword,
            RotatedLocalAdministratorPassword);
        using var rotationResponse = await client.SendAsync(rotation, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, rotationResponse.StatusCode);

        using var staleStatus = StatusRequest(oldToken);
        using var staleStatusResponse = await client.SendAsync(staleStatus, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, staleStatusResponse.StatusCode);
        var newToken = await LoginLocalAdministratorAsync(client, RotatedLocalAdministratorPassword);
        using var logout = new HttpRequestMessage(HttpMethod.Post, new Uri("/admin/auth/logout", UriKind.Relative));
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
        using var logoutResponse = await client.SendAsync(logout, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);
        using var loggedOutStatus = StatusRequest(newToken);
        using var loggedOutStatusResponse = await client.SendAsync(loggedOutStatus, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, loggedOutStatusResponse.StatusCode);
    }

    [Fact]
    public async Task LocalAdministratorCredentialPersistsAcrossRestart()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"post-edge-admin-restart-{Guid.NewGuid():N}.db");
        var settings = new Dictionary<string, string?>
        {
            ["Edge:LocalAdmin:AuthenticationEnabled"] = "true",
            ["Edge:LocalAdmin:InitialPassword"] = LocalAdministratorPassword
        };
        await using (var first = new EdgeApiFactory(
                         settings,
                         databasePath: databasePath,
                         deleteDatabaseOnDispose: false))
        {
            using var firstClient = first.CreateClient();
            var token = await LoginLocalAdministratorAsync(firstClient, LocalAdministratorPassword);
            using var rotation = PasswordRotationRequest(
                token,
                "admin",
                LocalAdministratorPassword,
                RotatedLocalAdministratorPassword);
            using var rotated = await firstClient.SendAsync(rotation, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, rotated.StatusCode);
        }

        await using var second = new EdgeApiFactory(settings, databasePath: databasePath);
        using var secondClient = second.CreateClient();
        using var oldLogin = await secondClient.PostAsJsonAsync(new Uri("/admin/auth/login", UriKind.Relative), new { userName = "admin", password = LocalAdministratorPassword }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        _ = await LoginLocalAdministratorAsync(secondClient, RotatedLocalAdministratorPassword);
    }

    [Fact]
    public async Task LocalAdministratorLoginRateLimiterRejectsRepeatedAttempts()
    {
        await using var factory = new EdgeApiFactory(new Dictionary<string, string?>
        {
            ["Edge:LocalAdmin:AuthenticationEnabled"] = "true",
            ["Edge:LocalAdmin:InitialPassword"] = LocalAdministratorPassword,
            ["Edge:LocalAdmin:LoginPermitLimit"] = "1"
        });
        using var client = factory.CreateClient();

        using var first = await client.PostAsJsonAsync(new Uri("/admin/auth/login", UriKind.Relative), new { userName = "unknown", password = LocalAdministratorPassword }, cancellationToken: TestContext.Current.CancellationToken);
        using var second = await client.PostAsJsonAsync(new Uri("/admin/auth/login", UriKind.Relative), new { userName = "unknown", password = LocalAdministratorPassword }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task ConnectionRateLimiterRejectsExcessHandshakeAttempts()
    {
        await using var factory = new EdgeApiFactory(new Dictionary<string, string?>
        {
            ["Edge:DeviceTransport:ConnectionPermitLimit"] = "1",
            ["Edge:DeviceTransport:ConnectionRateWindow"] = "00:01:00"
        });
        using var client = factory.CreateClient();

        using var first = await client.PostAsync(new Uri("/ws/device/negotiate?negotiateVersion=1", UriKind.Relative), null, TestContext.Current.CancellationToken);
        using var second = await client.PostAsync(new Uri("/ws/device/negotiate?negotiateVersion=1", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public void SimulatorArgumentsNeverRequirePrintingOrPersistingToken()
    {
        var options = SimulatorOptions.Parse(
            ["--url", "https://edge.test/ws/device", "--device", "D1", "--token", "secret", "--scenario", "auth"]);
        var environmentOptions = SimulatorOptions.Parse(
            ["--scenario", "full"],
            name => name == "POST_DEVICE_TOKEN" ? "environment-secret" : null);

        Assert.Equal("D1", options.DeviceId);
        Assert.Equal(SimulatorScenario.Auth, options.Scenario);
        Assert.Equal(1, options.Count);
        Assert.Equal("environment-secret", environmentOptions.DeviceToken);
        Assert.Throws<SimulatorUsageException>(() => SimulatorOptions.Parse([]));
    }

    [Fact]
    public async Task SimulatorRunsFullScenarioAgainstRealInProcessWebSocketHub()
    {
        await using var factory = new EdgeApiFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await SimulatorProgram.RunAsync(
            [
                "--url", "http://localhost/ws/device",
                "--device", DeviceId,
                "--token", DeviceToken,
                "--scenario", "full",
                "--timeout-seconds", "20"
            ],
            _ => factory.CreateHubConnection(),
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Contains("Dispatch closure succeeded.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        Assert.DoesNotContain(DeviceToken, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(DeviceToken, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulatorLoadModeProducesBoundedEvidenceAboveMockBaseline()
    {
        await using var factory = new EdgeApiFactory();
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await SimulatorProgram.RunAsync(
            [
                "--url", "http://localhost/ws/device",
                "--device", DeviceId,
                "--token", DeviceToken,
                "--scenario", "load",
                "--count", "20",
                "--timeout-seconds", "20"
            ],
            _ => factory.CreateHubConnection(),
            output,
            error);
        var evidence = output.ToString();
        var resultLine = evidence
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("LOAD_RESULT ", StringComparison.Ordinal));
        var values = resultLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(field => field.Split('=', 2))
            .ToDictionary(field => field[0], field => field[1], StringComparer.Ordinal);
        var throughput = decimal.Parse(
            values["throughputPerSecond"],
            System.Globalization.CultureInfo.InvariantCulture);
        var p50 = decimal.Parse(
            values["latencyP50Ms"],
            System.Globalization.CultureInfo.InvariantCulture);
        var p95 = decimal.Parse(
            values["latencyP95Ms"],
            System.Globalization.CultureInfo.InvariantCulture);
        var maximum = decimal.Parse(
            values["latencyMaxMs"],
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, exitCode);
        Assert.Equal("0", values["errors"]);
        Assert.True(throughput >= 2m);
        Assert.True(p50 <= p95);
        Assert.True(p95 <= maximum);
        Assert.Equal(string.Empty, error.ToString());
        Assert.DoesNotContain(DeviceToken, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulatorMapsHelpUsageTimeoutAndTransportFailureToStableExitCodes()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var unusedFactory = new Func<SimulatorOptions, HubConnection>(_ =>
            throw new InvalidOperationException("The help path must not create a connection."));

        var help = await SimulatorProgram.RunAsync(["--help"], unusedFactory, output, error);
        var usage = await SimulatorProgram.RunAsync(
            ["--token", "usage-secret", "--scenario", "invalid"],
            unusedFactory,
            output,
            error);
        var timedOut = await SimulatorProgram.RunAsync(
            ["--token", "timeout-secret", "--scenario", "auth"],
            _ => throw new OperationCanceledException(),
            output,
            error);
        var failed = await SimulatorProgram.RunAsync(
            ["--token", "transport-secret", "--scenario", "auth"],
            _ => throw new HttpRequestException("sensitive transport detail"),
            output,
            error);
        var defaultHelp = await SimulatorProgram.RunAsync(["--help"]);

        Assert.Equal(0, help);
        Assert.Equal(2, usage);
        Assert.Equal(3, timedOut);
        Assert.Equal(1, failed);
        Assert.Equal(0, defaultHelp);
        Assert.DoesNotContain("usage-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("timeout-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("transport-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive transport detail", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimulatorTopLevelEntryDelegatesHelpWithoutOpeningTransport()
    {
        var assembly = typeof(SimulatorProgram).Assembly;
        var program = assembly.GetType("Program");
        Assert.NotNull(program);
        var entry = program.GetMethod(
            "<Main>$",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(entry);
        object?[] parameters = [new string[] { "--help" }];

        var invocation = entry.Invoke(null, parameters);
        var task = Assert.IsAssignableFrom<Task<int>>(invocation);

        Assert.Equal(0, await task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SimulatorConnectionBuilderCreatesWebSocketOnlyDisconnectedClient()
    {
        var options = SimulatorOptions.Parse([
            "--url", "https://edge.test/ws/device",
            "--device", "D1",
            "--token", "test-token",
            "--scenario", "auth"
        ]);

        await using var connection = SimulatorProgram.CreateConnection(options);

        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public void SimulatorParserRejectsMalformedArgumentsAndSupportsHelp()
    {
        Assert.True(SimulatorOptions.Parse(["--help"]).ShowHelp);
        Assert.Throws<SimulatorUsageException>(() => SimulatorOptions.Parse(["--unsupported", "value"]));
        Assert.Throws<SimulatorUsageException>(() => SimulatorOptions.Parse(["--token"]));
        Assert.Throws<SimulatorUsageException>(() => SimulatorOptions.Parse([
            "--token", "one", "--token", "two"]));
        Assert.Throws<SimulatorUsageException>(() => SimulatorOptions.Parse([
            "--token", "value", "--url", "ftp://invalid"]));
        Assert.Throws<SimulatorUsageException>(() => SimulatorOptions.Parse([
            "--token", "value", "--device", " "]));
        Assert.Throws<SimulatorUsageException>(() => SimulatorOptions.Parse([
            "--token", "value", "--timeout-seconds", "301"]));
        Assert.Throws<SimulatorUsageException>(() => SimulatorOptions.Parse([
            "--token", "value", "--scenario", "load", "--count", "1001"]));
    }

    [Fact]
    public void ReceiveLimitFailsStartupWhenItCannotCarryApprovedProtocolPayload()
    {
        using var factory = new EdgeApiFactory(new Dictionary<string, string?>
        {
            ["Edge:DeviceTransport:MaximumReceiveMessageSizeBytes"] = "160000000"
        });

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    private static async Task<DeviceEnvelopeV1<DeviceAuthenticateResponseV1>> AuthenticateAsync(
        HubConnection connection) => await connection.InvokeAsync<DeviceEnvelopeV1<DeviceAuthenticateResponseV1>>(
        DeviceHubMethodsV1.Auth,
        Request(ContractMessageTypes.DeviceAuthenticate,
            new DeviceAuthenticateRequestV1(DeviceId, DeviceToken)));

    private static async Task<DeviceEnvelopeV1<DeviceProtocolErrorV1>> InvokeInboundErrorAsync(
        HubConnection connection,
        string barcode,
        IReadOnlyList<ParcelInlineImageV1> images) =>
        await connection.InvokeAsync<DeviceEnvelopeV1<DeviceProtocolErrorV1>>(
            DeviceHubMethodsV1.RegisterInbound,
            Request(ContractMessageTypes.DeviceParcelRead,
                new RegisterInboundRequestV1([barcode], DateTimeOffset.UnixEpoch, Images: images),
                Guid.NewGuid()));

    private static DeviceEnvelopeV1<TPayload> Request<TPayload>(
        string messageType,
        TPayload payload,
        Guid? operationId = null,
        Guid? correlationId = null) => DeviceEnvelopeV1.Create(
        messageType,
        correlationId ?? Guid.NewGuid(),
        DateTimeOffset.UnixEpoch,
        payload,
        operationId);

    private static HttpRequestMessage BindingRequest(string token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Put,
            new Uri($"/admin/devices/{DeviceId}/ip", UriKind.Relative))
        {
            Content = JsonContent.Create(new { ipAddresses = LoopbackAddresses })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage StatusRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/edge/status", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage BackupRequest(string token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/admin/storage/backup", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage PasswordRotationRequest(
        string token,
        string? userName,
        string? currentPassword,
        string? newPassword)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/admin/auth/password", UriKind.Relative))
        {
            Content = JsonContent.Create(new { userName, currentPassword, newPassword })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> LoginLocalAdministratorAsync(
        HttpClient client,
        string password)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/admin/auth/login", UriKind.Relative),
            new { userName = "admin", password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Assert.IsType<string>(payload.RootElement.GetProperty("accessToken").GetString());
    }

    private sealed class HostileFailureDeviceConnectionService : IDeviceConnectionService
    {
        public Task<Guid> ConnectedAsync(
            DeviceConnectedRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());

        public Task<DeviceAuthResponse> AuthenticateAsync(
            DeviceAuthRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(new DeviceAuthResponse(
                0,
                Guid.NewGuid(),
                DateTimeOffset.UnixEpoch,
                "AUTH_FAILED",
                "Rejected"));

        public Task DisconnectedAsync(
            DeviceDisconnectedRequest request,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingDestinationAssignmentService : IDestinationAssignmentService
    {
        public Task<DestinationAssignResponse> AssignAsync(
            DestinationAssignRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("Sensitive upstream destination failure.");
        }
    }
}
