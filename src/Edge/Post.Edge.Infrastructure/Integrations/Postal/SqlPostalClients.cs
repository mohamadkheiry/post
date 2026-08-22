using Microsoft.Extensions.Options;
using System.Globalization;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Integrations.Postal;

public sealed class SqlPostalInboundClient(
    IPostalSqlGateway gateway,
    IOptions<PostalSqlOptions> options,
    IClock clock) : IPostalInboundClient
{
    public async Task<PostalInboundResult> RegisterAsync(
        PostalInboundCommand command,
        CancellationToken cancellationToken)
    {
        var settings = ValidateSettings();
        var sorterId = await gateway.InsertInboundAsync(new SqlInboundInsert(
            command.Barcodes.CanonicalBarcode.Canonical,
            settings.UserId,
            settings.DeviceIp,
            command.ReadTimestamp,
            settings.SourceId), cancellationToken);
        var deadline = clock.UtcNow + command.Timeout;
        while (clock.UtcNow < deadline)
        {
            var state = await gateway.ReadInboundStateAsync(sorterId, cancellationToken);
            if (!state.Exists)
                return new PostalInboundResult(PostalInboundStatus.Failed, sorterId.ToString(CultureInfo.InvariantCulture), "POSTAL_ROW_MISSING", "Postal inbound row disappeared.");
            if (state.Error is not null)
            {
                return string.IsNullOrEmpty(state.Error)
                    ? new PostalInboundResult(PostalInboundStatus.Registered, sorterId.ToString(CultureInfo.InvariantCulture), null, null)
                    : new PostalInboundResult(
                        PostalInboundStatus.Failed,
                        sorterId.ToString(CultureInfo.InvariantCulture),
                        "POSTAL_REJECTED",
                        "Postal inbound registration was rejected.");
            }
            await Task.Delay(settings.PollInterval, cancellationToken);
        }
        return new PostalInboundResult(PostalInboundStatus.Pending, sorterId.ToString(CultureInfo.InvariantCulture), null, null);
    }

    public async Task<PostalInboundResult> GetPendingResultAsync(string trackingId, CancellationToken cancellationToken)
    {
        if (!long.TryParse(trackingId, out var sorterId))
            return new PostalInboundResult(PostalInboundStatus.Failed, trackingId, "INVALID_TRACKING_ID", "Postal tracking id is invalid.");
        var state = await gateway.ReadInboundStateAsync(sorterId, cancellationToken);
        if (!state.Exists)
            return new PostalInboundResult(PostalInboundStatus.Failed, trackingId, "POSTAL_ROW_MISSING", "Postal inbound row was not found.");
        if (state.Error is null) return new PostalInboundResult(PostalInboundStatus.Pending, trackingId, null, null);
        return string.IsNullOrEmpty(state.Error)
            ? new PostalInboundResult(PostalInboundStatus.Registered, trackingId, null, null)
            : new PostalInboundResult(
                PostalInboundStatus.Failed,
                trackingId,
                "POSTAL_REJECTED",
                "Postal inbound registration was rejected.");
    }

    private PostalSqlOptions ValidateSettings()
    {
        var settings = options.Value;
        if (settings.UserId <= 0 || settings.SourceId <= 0 || string.IsNullOrWhiteSpace(settings.DeviceIp))
            throw new InvalidOperationException("Postal SQL inbound settings are incomplete.");
        return settings;
    }
}

public sealed class SqlPostalBagClient(
    IPostalSqlGateway gateway,
    IOptions<PostalSqlOptions> options,
    IClock clock) : IPostalBagClient
{
    public async Task<PostalBagResult> CloseAsync(PostalBagCommand command, CancellationToken cancellationToken)
    {
        var settings = ValidateSettings();
        if (command.Parcels.Count == 0)
            return ProtocolError("The Edge bag request did not contain any parcels.");
        if (command.Parcels.Select(parcel => parcel.CanonicalBarcode).Distinct(StringComparer.Ordinal).Count() !=
            command.Parcels.Count)
        {
            return ProtocolError("The Edge bag request contained duplicate parcel barcodes.");
        }
        if (!int.TryParse(command.DestinationCode, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var destinationCode) ||
            command.DestinationCode.Length != 5)
            return ProtocolError("The Edge bag request contained an invalid destination code.");
        if (command.Parcels.Any(parcel => parcel.Weight is < 0 or > int.MaxValue))
            return ProtocolError("The Edge bag request contained an invalid parcel weight.");

        var totalWeightGrams = command.Parcels.Sum(parcel => parcel.Weight ?? 0);
        var sqlParcels = command.Parcels.Select(parcel => new SqlBagParcel(
            parcel.CanonicalBarcode,
            clock.UtcNow,
            checked((int)(parcel.Weight ?? 0)))).ToArray();
        var sqlResult = await gateway.CreateBagAsync(new SqlBagCommand(
            settings.SourceId,
            destinationCode,
            settings.BagServiceType,
            settings.BagType,
            settings.ServiceTypeId,
            (double)(totalWeightGrams / 1000m),
            sqlParcels,
            settings.PostUsername!,
            false), cancellationToken);
        var byBarcode = command.Parcels.ToDictionary(x => x.CanonicalBarcode, StringComparer.Ordinal);
        var failureBarcodes = sqlResult.Failures.Select(failure => failure.Barcode).ToArray();
        var countsAreConsistent = sqlResult.SuccessfulCount >= 0 && sqlResult.FailedCount >= 0 &&
                                  sqlResult.SuccessfulCount + sqlResult.FailedCount == command.Parcels.Count &&
                                  sqlResult.FailedCount == sqlResult.Failures.Count;
        var failureSetIsConsistent = failureBarcodes.Distinct(StringComparer.Ordinal).Count() == failureBarcodes.Length &&
                                     failureBarcodes.All(byBarcode.ContainsKey);
        if (!countsAreConsistent || !failureSetIsConsistent ||
            (sqlResult.SuccessfulCount > 0 && string.IsNullOrWhiteSpace(sqlResult.BagBarcode)))
        {
            return ProtocolError("Postal CreateBag returned inconsistent counts, failures, or bag identity.");
        }

        var failures = sqlResult.Failures.Select(x => new PostalParcelFailure(
            byBarcode[x.Barcode].ReadId,
            x.ErrorCode,
            $"Postal CreateBag error {x.ErrorCode}.",
            "Postal")).ToArray();
        return new PostalBagResult(
            PostalBagStatus.Completed,
            sqlResult.BagBarcode,
            failures,
            null,
            null);

        static PostalBagResult ProtocolError(string message) => new(
            PostalBagStatus.Failed,
            null,
            [],
            "POSTAL_PROTOCOL_ERROR",
            message);
    }

    private PostalSqlOptions ValidateSettings()
    {
        var settings = options.Value;
        if (settings.SourceId <= 0 || string.IsNullOrWhiteSpace(settings.PostUsername))
            throw new InvalidOperationException("Postal SQL bag settings are incomplete.");
        return settings;
    }
}

public sealed class SqlPostalDispatchClient(IPostalSqlGateway gateway, IOptions<PostalSqlOptions> options) : IPostalDispatchClient
{
    public async Task<PostalDispatchResult> CloseAsync(PostalDispatchCommand command, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.SourceId <= 0 || string.IsNullOrWhiteSpace(settings.PostUsername))
            throw new InvalidOperationException("Postal SQL dispatch settings are incomplete.");
        var result = await gateway.CreateBagAsync(new SqlBagCommand(
            settings.SourceId,
            int.Parse(command.DestinationCode, System.Globalization.CultureInfo.InvariantCulture),
            settings.BagServiceType,
            settings.BagType,
            settings.ServiceTypeId,
            0,
            [],
            settings.PostUsername,
            true), cancellationToken);
        return string.IsNullOrWhiteSpace(result.DispatchId)
            ? new PostalDispatchResult(false, null, "POSTAL_PROTOCOL_ERROR", "CreateBag omitted the dispatch id.")
            : new PostalDispatchResult(true, result.DispatchId, null, null);
    }
}
