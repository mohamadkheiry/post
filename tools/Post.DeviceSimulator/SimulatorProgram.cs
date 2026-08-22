using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Post.BuildingBlocks.Serialization;
using Post.Contracts;
using Post.Contracts.V1.Device;

namespace Post.DeviceSimulator;

public static class SimulatorProgram
{
    public static Task<int> RunAsync(IReadOnlyList<string> arguments) => RunAsync(
        arguments,
        CreateConnection,
        Console.Out,
        Console.Error);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Func<SimulatorOptions, HubConnection> connectionFactory,
        TextWriter output,
        TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            var options = SimulatorOptions.Parse(arguments);
            if (options.ShowHelp)
            {
                WriteHelp(output);
                return 0;
            }

            using var timeout = new CancellationTokenSource(options.Timeout);
            await RunScenarioAsync(options, connectionFactory, output, timeout.Token);
            await output.WriteLineAsync("Simulator scenario completed successfully.");
            return 0;
        }
        catch (SimulatorUsageException exception)
        {
            await error.WriteLineAsync(exception.Message);
            WriteHelp(error);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Simulator scenario timed out.");
            return 3;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(
                $"Simulator assertion or transport failure: {exception.GetType().Name}.");
            return 1;
        }
    }

    public static HubConnection CreateConnection(SimulatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new HubConnectionBuilder()
            .WithUrl(options.HubUrl, transport =>
            {
                transport.Transports = HttpTransportType.WebSockets;
                transport.SkipNegotiation = true;
            })
            .AddJsonProtocol(protocol => PostJsonDefaults.Apply(protocol.PayloadSerializerOptions))
            .Build();
    }

    private static async Task RunScenarioAsync(
        SimulatorOptions options,
        Func<SimulatorOptions, HubConnection> connectionFactory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await using var connection = connectionFactory(options);

        await connection.StartAsync(cancellationToken);
        await output.WriteLineAsync("WebSocket connection established.");

        var auth = await connection.InvokeAsync<DeviceEnvelopeV1<DeviceAuthenticateResponseV1>>(
            DeviceHubMethodsV1.Auth,
            Request(ContractMessageTypes.DeviceAuthenticate,
                new DeviceAuthenticateRequestV1(options.DeviceId, options.DeviceToken)),
            cancellationToken);
        Assert(auth.Payload.Status == DeviceStatusCodeV1.Success, "Authentication was rejected.");
        Assert(auth.Payload.SessionId is not null, "Authentication omitted sessionId.");
        await output.WriteLineAsync("Authentication succeeded.");

        if (options.Scenario == SimulatorScenario.Auth) return;

        if (options.Scenario == SimulatorScenario.Load)
        {
            await RunLoadScenarioAsync(connection, options.Count, output, cancellationToken);
            await connection.StopAsync(cancellationToken);
            return;
        }

        const string barcode = "595440000000001234500001";
        const string destination = "12345";
        var inboundOperation = Guid.NewGuid();
        var inboundPayload = new RegisterInboundRequestV1(
            [barcode],
            DateTimeOffset.UtcNow,
            new PhysicalAttributesV1(100, new ParcelDimensionsV1(100, 80, 60)),
            "MockParcel",
            Images:
            [
                new ParcelInlineImageV1("front", "AQID", "image/jpeg", "simulator")
            ],
            TimeoutMs: 5_000);
        var inbound = await connection.InvokeAsync<DeviceEnvelopeV1<RegisterInboundResponseV1>>(
            DeviceHubMethodsV1.RegisterInbound,
            Request(ContractMessageTypes.DeviceParcelRead,
                inboundPayload,
                inboundOperation),
            cancellationToken);
        Assert(inbound.Payload.Status == DeviceStatusCodeV1.Success, "Inbound registration failed.");
        Assert(inbound.OperationId == inboundOperation, "Inbound operationId was not echoed.");

        var inboundRetry = await connection.InvokeAsync<DeviceEnvelopeV1<RegisterInboundResponseV1>>(
            DeviceHubMethodsV1.RegisterInbound,
            Request(ContractMessageTypes.DeviceParcelRead,
                inboundPayload,
                inboundOperation),
            cancellationToken);
        Assert(inboundRetry.Payload.Status == inbound.Payload.Status, "Idempotent inbound retry changed status.");
        Assert(inboundRetry.OperationId == inboundOperation, "Idempotent inbound retry changed operationId.");
        await output.WriteLineAsync("Inbound registration and idempotent retry succeeded.");

        var assigned = await connection.InvokeAsync<DeviceEnvelopeV1<AssignDestinationResponseV1>>(
            DeviceHubMethodsV1.AssignDestination,
            Request(ContractMessageTypes.DeviceDestinationAssign,
                new AssignDestinationRequestV1(barcode, destination, "CHUTE-01"),
                Guid.NewGuid()),
            cancellationToken);
        Assert(assigned.Payload.Status == DestinationAssignmentStatusCodeV1.Confirmed,
            "Destination assignment failed.");
        await output.WriteLineAsync("Destination assignment succeeded.");

        var closedBag = await connection.InvokeAsync<DeviceEnvelopeV1<CloseBagResponseV1>>(
            DeviceHubMethodsV1.CloseBag,
            Request(ContractMessageTypes.DeviceBagClose,
                new CloseBagRequestV1(
                    destination,
                    "SEAL-SIMULATOR",
                    "Ground",
                    ChuteIds: ["CHUTE-01"],
                    ParcelTypes: ["MockParcel"],
                    Count: 999),
                Guid.NewGuid()),
            cancellationToken);
        Assert(closedBag.Payload.Status == DeviceStatusCodeV1.Success, "Bag closure failed.");
        Assert(closedBag.Payload.ResultType == BagCloseResultTypeV1.Completed, "Bag was not completed.");
        Assert(closedBag.Payload.Counts?.EffectiveCount == 1, "Bag counters were unexpected.");
        await output.WriteLineAsync("Bag closure succeeded.");

        var closedDispatch = await connection.InvokeAsync<DeviceEnvelopeV1<CloseDispatchResponseV1>>(
            DeviceHubMethodsV1.CloseDispatch,
            Request(ContractMessageTypes.DeviceDispatchClose,
                new CloseDispatchRequestV1(destination),
                Guid.NewGuid()),
            cancellationToken);
        Assert(closedDispatch.Payload.Status == DeviceStatusCodeV1.Success, "Dispatch closure failed.");
        Assert(closedDispatch.Payload.ResultType == DispatchCloseResultTypeV1.Completed,
            "Dispatch was not completed.");
        Assert(closedDispatch.Payload.BagCount == 1, "Dispatch bagCount was unexpected.");
        Assert(closedDispatch.Payload.TotalParcels == 1, "Dispatch totalParcels was unexpected.");
        await output.WriteLineAsync("Dispatch closure succeeded.");

        await connection.StopAsync(cancellationToken);
    }

    private static async Task RunLoadScenarioAsync(
        HubConnection connection,
        int count,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var latencyMilliseconds = new double[count];
        for (var index = 1; index <= count; index++)
        {
            var requestStarted = Stopwatch.GetTimestamp();
            var barcode = string.Concat(
                "59544",
                index.ToString("D9", System.Globalization.CultureInfo.InvariantCulture),
                "12345",
                index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture));
            var response = await connection.InvokeAsync<DeviceEnvelopeV1<RegisterInboundResponseV1>>(
                DeviceHubMethodsV1.RegisterInbound,
                Request(
                    ContractMessageTypes.DeviceParcelRead,
                    new RegisterInboundRequestV1(
                        [barcode],
                        DateTimeOffset.UtcNow,
                        ParcelType: "MockParcel"),
                    Guid.NewGuid()),
                cancellationToken);
            Assert(response.Payload.Status == DeviceStatusCodeV1.Success,
                "Inbound load scenario encountered a rejected parcel.");
            latencyMilliseconds[index - 1] = Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds;
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var throughput = count / Math.Max(elapsed.TotalSeconds, 0.001);
        Array.Sort(latencyMilliseconds);
        var p50 = latencyMilliseconds[Math.Max(0, (int)Math.Ceiling(count * 0.50) - 1)];
        var p95 = latencyMilliseconds[Math.Max(0, (int)Math.Ceiling(count * 0.95) - 1)];
        var maximum = latencyMilliseconds[^1];
        await output.WriteLineAsync(FormattableString.Invariant(
            $"LOAD_RESULT count={count} errors=0 elapsedMs={elapsed.TotalMilliseconds:F0} throughputPerSecond={throughput:F2} latencyP50Ms={p50:F2} latencyP95Ms={p95:F2} latencyMaxMs={maximum:F2}"));
    }

    private static DeviceEnvelopeV1<TPayload> Request<TPayload>(
        string messageType,
        TPayload payload,
        Guid? operationId = null) => DeviceEnvelopeV1.Create(
        messageType,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        payload,
        operationId);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine(
            "Post.DeviceSimulator --token <secret> [--url <hub-url>] [--device <id>] [--scenario auth|full|load] [--count 1..1000] [--timeout-seconds 1..300]");
        output.WriteLine("The token may instead be supplied through POST_DEVICE_TOKEN and is never printed.");
    }
}
