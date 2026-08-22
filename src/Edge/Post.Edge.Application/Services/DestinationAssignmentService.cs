using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.ValueObjects;
using Post.Contracts;
using Post.Contracts.V1.EdgeCore;

namespace Post.Edge.Application.Services;

public sealed class DestinationAssignmentService(
    IEdgeParcelRepository parcels,
    IOperationJournal operations,
    IOutboxWriter outbox,
    IEdgeUnitOfWork unitOfWork,
    IClock clock) : IDestinationAssignmentService
{
    private const string OperationType = "destination.assign";

    public async Task<DestinationAssignResponse> AssignAsync(
        DestinationAssignRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var identity = OperationIdentity.Resolve(request.OperationId, request.CorrelationId);
        var requestHash = Hash(request);
        var cached = await operations.GetAsync<DestinationAssignResponse>(
            identity.Value, OperationType, cancellationToken);
        if (cached is not null)
        {
            return string.Equals(cached.RequestHash, requestHash, StringComparison.Ordinal)
                ? cached.Response
                : new DestinationAssignResponse(
                    request.CorrelationId,
                    identity.Value,
                    2,
                    request.ReadId ?? Guid.Empty,
                    ErrorCode: "IDEMPOTENCY_CONFLICT",
                    ErrorMessage: "The operation id was already used for a different destination request.",
                    OperationIdIsBestEffort: identity.IsBestEffortFallback);
        }

        Domain.Parcels.EdgeParcel? parcel;
        try
        {
            parcel = request.ReadId is { } readId
                ? await parcels.FindByReadIdAsync(readId, cancellationToken)
                : string.IsNullOrWhiteSpace(request.Barcode)
                    ? null
                    : await parcels.FindByCanonicalBarcodeAsync(
                        Barcode.Parse(request.Barcode).Canonical,
                        cancellationToken);
        }
        catch (Domain.Common.DomainException ex)
        {
            return await SaveErrorAsync(
                request, identity, requestHash, "INVALID_BARCODE", ex.Message, cancellationToken);
        }
        if (parcel is null || !string.Equals(parcel.DeviceId, request.DeviceId, StringComparison.Ordinal))
        {
            return await SaveErrorAsync(
                request, identity, requestHash, "PARCEL_NOT_FOUND", "Parcel was not found for this device.", cancellationToken);
        }

        DestinationAssignResponse response;
        try
        {
            var destination = PostalCenterCode.Create(request.DestinationCode);
            parcel.AssignDestination(destination, request.Chute);
            response = new DestinationAssignResponse(
                request.CorrelationId,
                identity.Value,
                1,
                parcel.ReadId,
                destination.Value,
                parcel.ChuteId,
                OperationIdIsBestEffort: identity.IsBestEffortFallback);
            outbox.Enqueue(ApplicationSupport.Outbox(
                clock,
                "Core",
                ContractMessageTypes.EdgeParcelStatusChanged,
                new ParcelStatusChangedV1(
                    identity.Value,
                    parcel.CanonicalBarcode,
                    null,
                    CoreParcelLifecycleStatusV1.DestinationAssigned,
                    clock.UtcNow,
                    destination.Value,
                    parcel.ChuteId),
                $"destination:{identity.Value:N}"));
        }
        catch (Exception ex) when (ex is ArgumentException or Domain.Common.DomainException)
        {
            response = new DestinationAssignResponse(
                request.CorrelationId,
                identity.Value,
                2,
                parcel.ReadId,
                ErrorCode: "INVALID_DESTINATION",
                ErrorMessage: ex.Message,
                OperationIdIsBestEffort: identity.IsBestEffortFallback);
        }

        operations.AddResponse(identity.Value, OperationType, response, clock.UtcNow, requestHash);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return response;
    }

    private async Task<DestinationAssignResponse> SaveErrorAsync(
        DestinationAssignRequest request,
        OperationIdentity identity,
        string requestHash,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var response = new DestinationAssignResponse(
            request.CorrelationId,
            identity.Value,
            2,
            request.ReadId ?? Guid.Empty,
            ErrorCode: code,
            ErrorMessage: message,
            OperationIdIsBestEffort: identity.IsBestEffortFallback);
        operations.AddResponse(identity.Value, OperationType, response, clock.UtcNow, requestHash);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return response;
    }

    private static string Hash(DestinationAssignRequest request) => ApplicationSupport.RequestHash(new
    {
        request.DeviceId,
        request.ReadId,
        Barcode = request.Barcode?.Trim(),
        DestinationCode = request.DestinationCode?.Trim(),
        Chute = request.Chute?.Trim()
    });
}
