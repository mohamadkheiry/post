using System.Buffers;
using System.Globalization;
using System.Net;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Post.Contracts;
using Post.Contracts.V1.Device;
using Post.Edge.Application.Services;
using AppContracts = Post.Edge.Application.Contracts;

namespace Post.Edge.Api;

public sealed class DeviceHub(
    IDeviceConnectionService connections,
    IInboundRegistrationService inbound,
    IDestinationAssignmentService destinations,
    IBagClosingService bags,
    IDispatchClosingService dispatches,
    TimeProvider timeProvider,
    IOptions<EdgeDeviceTransportOptions> transportOptions,
    IHostEnvironment environment,
    ILogger<DeviceHub> logger) : Hub
{
    private const string AuthenticatedDeviceKey = "edge.authenticated-device";
    private const string SessionIdKey = "edge.session-id";
    private const string SourceIpKey = "edge.source-ip";
    private const string AuthenticationDeadlineSourceKey = "edge.auth-deadline-source";
    private const string AuthenticationDeadlineTaskKey = "edge.auth-deadline-task";
    private const string FailedAuthenticationCloseTaskKey = "edge.failed-auth-close-task";
    private const string AuthenticationRejectedKey = "edge.auth-rejected";

    private static readonly Action<ILogger, string, string, Exception?> LogOperationFailure = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(4201, "EdgeDeviceOperationFailed"),
        "Device operation {Operation} failed safely for connection {ConnectionId}.");

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The SignalR connection boundary must fail closed without exposing device data.")]
    public override async Task OnConnectedAsync()
    {
        var sourceIp = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString();
        if (string.IsNullOrWhiteSpace(sourceIp) &&
            (environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            sourceIp = IPAddress.Loopback.ToString();
        }

        if (string.IsNullOrWhiteSpace(sourceIp))
        {
            Context.Abort();
            return;
        }

        try
        {
            var sessionId = await connections.ConnectedAsync(new AppContracts.DeviceConnectedRequest(
                string.Empty,
                sourceIp,
                Context.ConnectionId),
                Context.ConnectionAborted);
            Context.Items[SessionIdKey] = sessionId;
            Context.Items[SourceIpKey] = sourceIp;
            var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
            Context.Items[AuthenticationDeadlineSourceKey] = deadlineSource;
            Context.Items[AuthenticationDeadlineTaskKey] = EnforceAuthenticationDeadlineAsync(
                Context,
                deadlineSource.Token);
            await base.OnConnectedAsync();
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            LogOperationFailure(logger, "connect", Context.ConnectionId, null);
            Context.Abort();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Disconnect cleanup must complete without exposing device data.")]
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await CancelAuthenticationDeadlineAsync();
        if (Context.Items.TryGetValue(FailedAuthenticationCloseTaskKey, out var closeValue) &&
            closeValue is Task closeTask)
        {
            await closeTask;
        }

        try
        {
            var deviceId = Context.Items.TryGetValue(AuthenticatedDeviceKey, out var value)
                ? value as string ?? string.Empty
                : string.Empty;
            await connections.DisconnectedAsync(
                new AppContracts.DeviceDisconnectedRequest(deviceId, Context.ConnectionId),
                CancellationToken.None);
        }
        catch (Exception)
        {
            LogOperationFailure(logger, "disconnect", Context.ConnectionId, null);
        }

        await base.OnDisconnectedAsync(exception);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The device boundary maps failures to a safe response and never logs the token.")]
    public async Task<object> Auth(DeviceEnvelopeV1<DeviceAuthenticateRequestV1> envelope)
    {
        var invalid = ValidateEnvelope(envelope, ContractMessageTypes.DeviceAuthenticate);
        if (invalid is not null)
        {
            await ScheduleFailedAuthenticationCloseAsync();
            return invalid;
        }
        if (string.IsNullOrWhiteSpace(envelope.Payload.DeviceId) ||
            string.IsNullOrWhiteSpace(envelope.Payload.DeviceToken))
        {
            await ScheduleFailedAuthenticationCloseAsync();
            return ProtocolError(envelope, "MISSING_REQUIRED_FIELD",
                "Authentication requires deviceId and deviceToken.", "payload");
        }
        if (envelope.Payload.DeviceId.Length > AppContracts.DeviceAuthenticationLimits.MaximumDeviceIdCharacters ||
            envelope.Payload.DeviceToken.Length > AppContracts.DeviceAuthenticationLimits.MaximumDeviceTokenCharacters)
        {
            await ScheduleFailedAuthenticationCloseAsync();
            return ProtocolError(envelope, "VALIDATION_ERROR",
                "Authentication fields exceed the supported length.", "payload");
        }
        if (Context.Items.ContainsKey(AuthenticationRejectedKey))
            return ProtocolError(envelope, "VALIDATION_ERROR", "Authentication has already failed on this connection.");

        if (Context.Items.TryGetValue(AuthenticatedDeviceKey, out var authenticated))
        {
            if (!string.Equals(authenticated as string, envelope.Payload.DeviceId, StringComparison.Ordinal))
            {
                await ScheduleFailedAuthenticationCloseAsync();
                return ProtocolError(envelope, "VALIDATION_ERROR",
                    "This connection is already authenticated for another device.", "payload.deviceId");
            }

            var existingSession = Context.Items.TryGetValue(SessionIdKey, out var sessionValue) && sessionValue is Guid value
                ? value
                : (Guid?)null;
            return Response(envelope, new DeviceAuthenticateResponseV1(
                DeviceStatusCodeV1.Success,
                existingSession,
                timeProvider.GetUtcNow()),
                envelope.OperationId);
        }

        try
        {
            var sourceIp = GetSourceIp();
            var result = await connections.AuthenticateAsync(new AppContracts.DeviceAuthRequest(
                envelope.Payload.DeviceId,
                envelope.Payload.DeviceToken,
                sourceIp,
                Context.ConnectionId),
                Context.ConnectionAborted);
            if (result.IsSuccess)
            {
                Context.Items[AuthenticatedDeviceKey] = envelope.Payload.DeviceId;
                if (result.SessionId is { } sessionId) Context.Items[SessionIdKey] = sessionId;
                await CancelAuthenticationDeadlineAsync();
            }
            else
            {
                await ScheduleFailedAuthenticationCloseAsync();
            }

            return Response(envelope, new DeviceAuthenticateResponseV1(
                result.IsSuccess ? DeviceStatusCodeV1.Success : DeviceStatusCodeV1.Error,
                result.IsSuccess ? result.SessionId : null,
                result.IsSuccess ? result.EdgeServerTime : null,
                result.IsSuccess ? null : "Device authentication failed."),
                envelope.OperationId);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            LogOperationFailure(logger, ContractMessageTypes.DeviceAuthenticate, Context.ConnectionId, null);
            await ScheduleFailedAuthenticationCloseAsync();
            return Response(envelope, new DeviceAuthenticateResponseV1(
                DeviceStatusCodeV1.Error,
                ErrorMessage: "Device authentication failed."),
                envelope.OperationId);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The device boundary maps dependency and validation failures to a safe response.")]
    public async Task<object> RegisterInbound(DeviceEnvelopeV1<RegisterInboundRequestV1> envelope)
    {
        var invalid = ValidateAuthenticatedEnvelope(envelope, ContractMessageTypes.DeviceParcelRead, out var deviceId);
        if (invalid is not null) return invalid;
        if (envelope.Payload.Barcodes is null || envelope.Payload.Barcodes.Count == 0)
            return ProtocolError(envelope, "MISSING_REQUIRED_FIELD", "At least one barcode is required.", "payload.barcodes");
        var imageValidationError = ValidateImages(envelope.Payload.Images);
        if (imageValidationError is not null)
            return ProtocolError(envelope, imageValidationError.Value.Code,
                imageValidationError.Value.Message, "payload.images");

        try
        {
            var physical = envelope.Payload.PhysicalAttributes;
            var dimensions = physical?.Dimensions;
            var result = await inbound.RegisterAsync(new AppContracts.InboundParcelRequest(
                envelope.CorrelationId,
                envelope.OperationId,
                deviceId,
                envelope.Payload.Barcodes,
                envelope.Payload.ReadTimestamp,
                physical?.WeightGrams,
                dimensions?.LengthMm,
                dimensions?.WidthMm,
                dimensions?.HeightMm,
                envelope.Payload.ParcelType,
                null,
                SupplementaryData: envelope.Payload.SupplementaryData is { } supplementary
                    ? supplementary.Clone()
                    : null,
                Images: envelope.Payload.Images?.Select(image => new AppContracts.InboundImageDto(
                    image.ImageId,
                    image.ContentBase64,
                    image.MimeType,
                    image.Description)).ToArray(),
                TimeoutMs: envelope.Payload.TimeoutMs),
                Context.ConnectionAborted);
            return Response(envelope, new RegisterInboundResponseV1(
                MapInboundStatus(result.Status),
                result.OriginCode,
                result.DestinationCode,
                MapDiscrepancy(result.DiscrepancyCode),
                ErrorMessage: result.ErrorMessage,
                OperationIdIsBestEffort: result.OperationIdIsBestEffort),
                result.OperationId);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            LogOperationFailure(logger, ContractMessageTypes.DeviceParcelRead, Context.ConnectionId, null);
            return Response(envelope, new RegisterInboundResponseV1(
                DeviceStatusCodeV1.Error,
                ErrorMessage: "Inbound registration failed.",
                OperationIdIsBestEffort: envelope.OperationId is null),
                EffectiveOperationId(envelope));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The device boundary maps dependency and validation failures to a safe response.")]
    public async Task<object> AssignDestination(DeviceEnvelopeV1<AssignDestinationRequestV1> envelope)
    {
        var invalid = ValidateAuthenticatedEnvelope(envelope, ContractMessageTypes.DeviceDestinationAssign, out var deviceId);
        if (invalid is not null) return invalid;
        if (string.IsNullOrWhiteSpace(envelope.Payload.Barcode) ||
            string.IsNullOrWhiteSpace(envelope.Payload.DestinationCenterCode))
            return ProtocolError(envelope, "MISSING_REQUIRED_FIELD",
                "Destination assignment requires barcode and destinationCenterCode.", "payload");

        try
        {
            var result = await destinations.AssignAsync(new AppContracts.DestinationAssignRequest(
                envelope.CorrelationId,
                envelope.OperationId,
                deviceId,
                null,
                envelope.Payload.DestinationCenterCode,
                envelope.Payload.ChuteId,
                envelope.Payload.Barcode),
                Context.ConnectionAborted);
            return Response(envelope, new AssignDestinationResponseV1(
                result.IsSuccess
                    ? DestinationAssignmentStatusCodeV1.Confirmed
                    : DestinationAssignmentStatusCodeV1.Error,
                result.ErrorMessage,
                result.OperationIdIsBestEffort),
                result.OperationId);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            LogOperationFailure(logger, ContractMessageTypes.DeviceDestinationAssign, Context.ConnectionId, null);
            return Response(envelope, new AssignDestinationResponseV1(
                DestinationAssignmentStatusCodeV1.Error,
                "Destination assignment failed.",
                envelope.OperationId is null),
                EffectiveOperationId(envelope));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The device boundary maps dependency and validation failures to a safe response.")]
    public async Task<object> CloseBag(DeviceEnvelopeV1<CloseBagRequestV1> envelope)
    {
        var invalid = ValidateAuthenticatedEnvelope(envelope, ContractMessageTypes.DeviceBagClose, out var deviceId);
        if (invalid is not null) return invalid;
        if (string.IsNullOrWhiteSpace(envelope.Payload.DestinationCenterCode) ||
            string.IsNullOrWhiteSpace(envelope.Payload.SealNumber) ||
            string.IsNullOrWhiteSpace(envelope.Payload.TransportType))
            return ProtocolError(envelope, "MISSING_REQUIRED_FIELD",
                "Bag closure requires destinationCenterCode, sealNumber, and transportType.", "payload");

        try
        {
            var result = await bags.CloseAsync(new AppContracts.CloseBagRequest(
                envelope.CorrelationId,
                envelope.OperationId,
                deviceId,
                envelope.Payload.DestinationCenterCode,
                null,
                envelope.Payload.LastBarcode,
                envelope.Payload.Count,
                null,
                null,
                envelope.Payload.SealNumber,
                envelope.Payload.TransportType,
                envelope.Payload.ChuteIds,
                envelope.Payload.ParcelTypes,
                envelope.Payload.ServiceTypes?.Select(service => service.ToString(CultureInfo.InvariantCulture)).ToArray()),
                Context.ConnectionAborted);
            var resultType = result.Result switch
            {
                AppContracts.BagCloseResult.Completed => BagCloseResultTypeV1.Completed,
                AppContracts.BagCloseResult.LabelPending => BagCloseResultTypeV1.LabelPending,
                AppContracts.BagCloseResult.NoEligibleParcel => BagCloseResultTypeV1.NoEligibleParcels,
                AppContracts.BagCloseResult.AllParcelsFailed => BagCloseResultTypeV1.AllParcelsFailed,
                _ => BagCloseResultTypeV1.Error
            };
            return Response(envelope, new CloseBagResponseV1(
                resultType == BagCloseResultTypeV1.Error ? DeviceStatusCodeV1.Error : DeviceStatusCodeV1.Success,
                resultType,
                envelope.Payload.DestinationCenterCode,
                result.BagBarcode,
                result.LabelPayload,
                new BagClosureCountsV1(result.Counts.N, result.Counts.P, result.Counts.M, result.Counts.Q),
                result.Errors.Select(error => new BagParcelErrorV1(
                    error.CanonicalBarcode,
                    error.ErrorCode,
                    error.Description,
                    error.Category ?? "Operational")).ToArray(),
                result.ErrorMessage,
                result.OperationIdIsBestEffort),
                result.OperationId);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            LogOperationFailure(logger, ContractMessageTypes.DeviceBagClose, Context.ConnectionId, null);
            return Response(envelope, new CloseBagResponseV1(
                DeviceStatusCodeV1.Error,
                BagCloseResultTypeV1.Error,
                envelope.Payload.DestinationCenterCode,
                ErrorMessage: "Bag closure failed.",
                OperationIdIsBestEffort: envelope.OperationId is null),
                EffectiveOperationId(envelope));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The device boundary maps dependency and validation failures to a safe response.")]
    public async Task<object> CloseDispatch(DeviceEnvelopeV1<CloseDispatchRequestV1> envelope)
    {
        var invalid = ValidateAuthenticatedEnvelope(envelope, ContractMessageTypes.DeviceDispatchClose, out var deviceId);
        if (invalid is not null) return invalid;
        if (string.IsNullOrWhiteSpace(envelope.Payload.DestinationCenterCode))
            return ProtocolError(envelope, "MISSING_REQUIRED_FIELD",
                "Dispatch closure requires destinationCenterCode.", "payload.destinationCenterCode");

        try
        {
            var result = await dispatches.CloseAsync(new AppContracts.CloseDispatchRequest(
                envelope.CorrelationId,
                envelope.OperationId,
                deviceId,
                envelope.Payload.DestinationCenterCode),
                Context.ConnectionAborted);
            return Response(envelope, new CloseDispatchResponseV1(
                result.IsSuccess ? DeviceStatusCodeV1.Success : DeviceStatusCodeV1.Error,
                result.IsSuccess ? DispatchCloseResultTypeV1.Completed : DispatchCloseResultTypeV1.Error,
                envelope.Payload.DestinationCenterCode,
                DispatchId: result.IsSuccess ? result.DispatchId : null,
                BagCount: result.IsSuccess ? result.BagBarcodes.Count : null,
                TotalParcels: result.IsSuccess ? result.TotalParcels : null,
                ProblemParcels: result.IsSuccess
                    ? result.ProblemParcels.Select(problem => new DispatchProblemParcelV1(
                        problem.Barcode,
                        problem.ErrorCode,
                        problem.Description,
                        problem.BagBarcode)).ToArray()
                    : null,
                ErrorMessage: result.ErrorMessage,
                OperationIdIsBestEffort: result.OperationIdIsBestEffort),
                result.OperationId);
        }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            LogOperationFailure(logger, ContractMessageTypes.DeviceDispatchClose, Context.ConnectionId, null);
            return Response(envelope, new CloseDispatchResponseV1(
                DeviceStatusCodeV1.Error,
                DispatchCloseResultTypeV1.Error,
                envelope.Payload.DestinationCenterCode,
                ErrorMessage: "Dispatch closure failed.",
                OperationIdIsBestEffort: envelope.OperationId is null),
                EffectiveOperationId(envelope));
        }
    }

    private DeviceEnvelopeV1<DeviceProtocolErrorV1>? ValidateAuthenticatedEnvelope<TPayload>(
        DeviceEnvelopeV1<TPayload> envelope,
        string messageType,
        out string deviceId)
    {
        var invalid = ValidateEnvelope(envelope, messageType);
        if (invalid is not null)
        {
            deviceId = string.Empty;
            return invalid;
        }

        if (!Context.Items.TryGetValue(AuthenticatedDeviceKey, out var value) || value is not string authenticatedDevice)
        {
            deviceId = string.Empty;
            return ProtocolError(envelope, "VALIDATION_ERROR",
                "Authenticate before invoking this operation.", "authentication");
        }

        deviceId = authenticatedDevice;
        return null;
    }

    private DeviceEnvelopeV1<DeviceProtocolErrorV1>? ValidateEnvelope<TPayload>(
        DeviceEnvelopeV1<TPayload> envelope,
        string expectedMessageType)
    {
        if (envelope is null)
            return ProtocolError<TPayload>(null, "MISSING_REQUIRED_FIELD", "A request envelope is required.", "envelope");
        if (!string.Equals(envelope.ProtocolVersion, ContractVersions.V1, StringComparison.Ordinal))
            return ProtocolError(envelope, "UNSUPPORTED_PROTOCOL_VERSION", "Only protocol version 1.0 is supported.");
        if (!string.Equals(envelope.MessageType, expectedMessageType, StringComparison.Ordinal))
            return ProtocolError(envelope, "UNKNOWN_MESSAGE_TYPE",
                "The envelope messageType does not match the Hub method.", "messageType");
        if (envelope.CorrelationId == Guid.Empty)
            return ProtocolError(envelope, "VALIDATION_ERROR",
                "correlationId must be a non-empty GUID.", "correlationId");
        if (envelope.OperationId == Guid.Empty)
            return ProtocolError(envelope, "VALIDATION_ERROR",
                "operationId must be omitted or a non-empty GUID.", "operationId");
        if (envelope.Payload is null)
            return ProtocolError(envelope, "MISSING_REQUIRED_FIELD", "A request payload is required.", "payload");
        return null;
    }

    private DeviceEnvelopeV1<TResponse> Response<TRequest, TResponse>(
        DeviceEnvelopeV1<TRequest> request,
        TResponse payload,
        Guid? operationId) => DeviceEnvelopeV1.Create(
            request.MessageType,
            request.CorrelationId,
            timeProvider.GetUtcNow(),
            payload,
            operationId);

    private DeviceEnvelopeV1<DeviceProtocolErrorV1> ProtocolError<TPayload>(
        DeviceEnvelopeV1<TPayload>? request,
        string code,
        string message,
        string? details = null)
    {
        var correlationId = request is not null && request.CorrelationId != Guid.Empty
            ? request.CorrelationId
            : Guid.NewGuid();
        var operationId = request?.OperationId == Guid.Empty ? null : request?.OperationId;
        return DeviceEnvelopeV1.Create(
            ContractMessageTypes.DeviceProtocolError,
            correlationId,
            timeProvider.GetUtcNow(),
            new DeviceProtocolErrorV1(code, message, details),
            operationId);
    }

    private async Task CancelAuthenticationDeadlineAsync()
    {
        Context.Items.TryGetValue(AuthenticationDeadlineSourceKey, out var sourceValue);
        Context.Items.TryGetValue(AuthenticationDeadlineTaskKey, out var taskValue);
        Context.Items.Remove(AuthenticationDeadlineSourceKey);
        Context.Items.Remove(AuthenticationDeadlineTaskKey);

        if (sourceValue is CancellationTokenSource source)
        {
            await source.CancelAsync();
        }
        if (taskValue is Task task)
        {
            await task;
        }
        if (sourceValue is CancellationTokenSource disposableSource) disposableSource.Dispose();
    }

    private async Task ScheduleFailedAuthenticationCloseAsync()
    {
        Context.Items[AuthenticationRejectedKey] = true;
        await CancelAuthenticationDeadlineAsync();
        if (Context.Items.ContainsKey(FailedAuthenticationCloseTaskKey)) return;
        Context.Items[FailedAuthenticationCloseTaskKey] = AbortAfterDelayAsync(
            Context,
            transportOptions.Value.FailedAuthenticationCloseDelay);
    }

    private async Task EnforceAuthenticationDeadlineAsync(
        HubCallerContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(transportOptions.Value.AuthenticationDeadline, cancellationToken);
            context.Abort();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task AbortAfterDelayAsync(HubCallerContext context, TimeSpan delay)
    {
        await Task.Delay(delay);
        context.Abort();
    }

    private string GetSourceIp() => Context.Items.TryGetValue(SourceIpKey, out var value) && value is string sourceIp
        ? sourceIp
        : throw new InvalidOperationException("Connection source address is unavailable.");

    private static Guid EffectiveOperationId<TPayload>(DeviceEnvelopeV1<TPayload> envelope) =>
        envelope.OperationId ?? envelope.CorrelationId;

    private static DeviceStatusCodeV1 MapInboundStatus(AppContracts.InboundResultStatus status) => status switch
    {
        AppContracts.InboundResultStatus.Registered => DeviceStatusCodeV1.Success,
        AppContracts.InboundResultStatus.Pending => DeviceStatusCodeV1.Pending,
        AppContracts.InboundResultStatus.Returned => DeviceStatusCodeV1.Returned,
        AppContracts.InboundResultStatus.Refused => DeviceStatusCodeV1.Refused,
        _ => DeviceStatusCodeV1.Error
    };

    private static DeviceDiscrepancyV1? MapDiscrepancy(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var weight = code.Contains("weight", StringComparison.OrdinalIgnoreCase);
        var dimension = code.Contains("dimension", StringComparison.OrdinalIgnoreCase) ||
                        code.Contains("volume", StringComparison.OrdinalIgnoreCase);
        return weight || dimension ? new DeviceDiscrepancyV1(weight, dimension) : null;
    }

    private static (string Code, string Message)? ValidateImages(
        IReadOnlyList<ParcelInlineImageV1>? images)
    {
        if (images is null) return null;
        if (images.Count > AppContracts.InboundEvidenceLimits.MaximumImageCount)
            return ("VALIDATION_ERROR", "At most 32 images are accepted per inbound read.");

        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in images)
        {
            if (image is null)
                return ("MISSING_REQUIRED_FIELD", "Image items cannot be null.");
            if (string.IsNullOrWhiteSpace(image.ImageId) ||
                image.ImageId.Length > 128 ||
                !imageIds.Add(image.ImageId))
                return ("VALIDATION_ERROR", "Image ids must be unique, non-empty, and at most 128 characters.");
            if (string.IsNullOrWhiteSpace(image.ContentBase64))
                return ("MISSING_REQUIRED_FIELD", "Image content is required.");
            if (image.ContentBase64.Length > AppContracts.InboundEvidenceLimits.MaximumImageBase64Characters)
                return ("VALIDATION_ERROR", "An image exceeds the 5,000,000-character Base64 limit.");
            if (!IsValidImageBase64(image.ContentBase64))
                return ("VALIDATION_ERROR", "Image content is not valid Base64 or exceeds the decoded-size limit.");
            if (image.MimeType is { Length: > 255 } || image.MimeType?.IndexOfAny(['\r', '\n']) >= 0)
                return ("VALIDATION_ERROR", "Image MIME type is invalid.");
            if (image.Description is { Length: > 1_024 })
                return ("VALIDATION_ERROR", "Image description is too long.");
        }

        return null;
    }

    private static bool IsValidImageBase64(string value)
    {
        var maximumDecodedLength = checked(((value.Length + 3) / 4) * 3);
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, maximumDecodedLength));
        try
        {
            return Convert.TryFromBase64String(value, buffer, out var bytesWritten) &&
                   bytesWritten <= AppContracts.InboundEvidenceLimits.MaximumDecodedImageBytes;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
