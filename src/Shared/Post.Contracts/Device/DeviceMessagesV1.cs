using System.Text.Json;

namespace Post.Contracts.V1.Device;

/// <summary>Device authentication request. It is the first operation on a connection.</summary>
/// <param name="DeviceId">Registered device identifier.</param>
/// <param name="DeviceToken">Opaque secret that must never be logged.</param>
public sealed record DeviceAuthenticateRequestV1(string DeviceId, string DeviceToken);

/// <summary>Device authentication response.</summary>
/// <param name="Status">Success or error from the comprehensive numeric status table.</param>
/// <param name="SessionId">New session identifier on success.</param>
/// <param name="EdgeServerTime">Edge time returned for device clock synchronization.</param>
/// <param name="ErrorMessage">Safe failure message.</param>
public sealed record DeviceAuthenticateResponseV1(
    DeviceStatusCodeV1 Status,
    Guid? SessionId = null,
    DateTimeOffset? EdgeServerTime = null,
    string? ErrorMessage = null);

/// <summary>Parcel dimensions in millimetres.</summary>
/// <param name="LengthMm">Length.</param>
/// <param name="WidthMm">Width.</param>
/// <param name="HeightMm">Height.</param>
public sealed record ParcelDimensionsV1(int? LengthMm = null, int? WidthMm = null, int? HeightMm = null);

/// <summary>Optional physical attributes captured by a device.</summary>
/// <param name="WeightGrams">Weight in grams.</param>
/// <param name="Dimensions">Optional dimensions.</param>
public sealed record PhysicalAttributesV1(int? WeightGrams = null, ParcelDimensionsV1? Dimensions = null);

/// <summary>Inline image defined by device protocol V1.</summary>
/// <param name="ImageId">Device-assigned image identifier.</param>
/// <param name="ContentBase64">Base64 image content.</param>
/// <param name="MimeType">Optional media type.</param>
/// <param name="Description">Optional view description such as x-ray.</param>
public sealed record ParcelInlineImageV1(
    string ImageId,
    string ContentBase64,
    string? MimeType = null,
    string? Description = null);

/// <summary>RegisterInbound payload from the approved device protocol.</summary>
/// <param name="Barcodes">One or more 14/24-digit barcodes.</param>
/// <param name="ReadTimestamp">Device reading timestamp.</param>
/// <param name="PhysicalAttributes">Optional measurements.</param>
/// <param name="ParcelType">Optional parcel type.</param>
/// <param name="SupplementaryData">Optional non-image JSON object.</param>
/// <param name="Images">Optional inline images; schema validation limits this to 32.</param>
/// <param name="TimeoutMs">Optional real-time response deadline.</param>
public sealed record RegisterInboundRequestV1(
    IReadOnlyList<string> Barcodes,
    DateTimeOffset ReadTimestamp,
    PhysicalAttributesV1? PhysicalAttributes = null,
    string? ParcelType = null,
    JsonElement? SupplementaryData = null,
    IReadOnlyList<ParcelInlineImageV1>? Images = null,
    int? TimeoutMs = null);

/// <summary>Optional weight or volume discrepancy returned for a reading.</summary>
/// <param name="WeightMismatch">Whether weight differs from reference data.</param>
/// <param name="DimensionMismatch">Whether dimensions differ from reference data.</param>
/// <param name="ReferenceWeightGrams">Reference weight, when available.</param>
/// <param name="MeasuredWeightGrams">Measured weight, when available.</param>
public sealed record DeviceDiscrepancyV1(
    bool WeightMismatch,
    bool DimensionMismatch,
    int? ReferenceWeightGrams = null,
    int? MeasuredWeightGrams = null);

/// <summary>RegisterInbound response.</summary>
/// <param name="Status">Numeric protocol status.</param>
/// <param name="OriginCode">Five-digit origin when resolvable.</param>
/// <param name="DestinationCode">Five-digit destination when resolvable.</param>
/// <param name="Discrepancy">Optional discrepancy.</param>
/// <param name="ServiceType">Optional main postal service category when reference data is available.</param>
/// <param name="ErrorMessage">Safe error/return explanation.</param>
/// <param name="OperationIdIsBestEffort">True when correlationId was used because operationId was omitted.</param>
public sealed record RegisterInboundResponseV1(
    DeviceStatusCodeV1 Status,
    string? OriginCode = null,
    string? DestinationCode = null,
    DeviceDiscrepancyV1? Discrepancy = null,
    string? ServiceType = null,
    string? ErrorMessage = null,
    bool? OperationIdIsBestEffort = null);

/// <summary>AssignDestination request.</summary>
/// <param name="Barcode">Parcel barcode.</param>
/// <param name="DestinationCenterCode">Five-digit destination.</param>
/// <param name="ChuteId">Optional chute identifier.</param>
public sealed record AssignDestinationRequestV1(
    string Barcode,
    string DestinationCenterCode,
    string? ChuteId = null);

/// <summary>AssignDestination response using its dedicated numeric status table.</summary>
/// <param name="Status">Confirmed or error.</param>
/// <param name="ErrorMessage">Safe failure message.</param>
/// <param name="OperationIdIsBestEffort">True when correlationId was used because operationId was omitted.</param>
public sealed record AssignDestinationResponseV1(
    DestinationAssignmentStatusCodeV1 Status,
    string? ErrorMessage = null,
    bool? OperationIdIsBestEffort = null);

/// <summary>CloseBag request from the approved device protocol.</summary>
/// <param name="DestinationCenterCode">Required destination filter.</param>
/// <param name="SealNumber">Physical seal number.</param>
/// <param name="TransportType">Transport mode.</param>
/// <param name="ChuteIds">Optional chute filters.</param>
/// <param name="ParcelTypes">Optional parcel-type filters.</param>
/// <param name="ServiceTypes">Optional numeric service filters.</param>
/// <param name="Count">Informational expected count; it is not a selection limit.</param>
/// <param name="LastBarcode">Optional final-barcode filter.</param>
public sealed record CloseBagRequestV1(
    string DestinationCenterCode,
    string SealNumber,
    string TransportType,
    IReadOnlyList<string>? ChuteIds = null,
    IReadOnlyList<string>? ParcelTypes = null,
    IReadOnlyList<int>? ServiceTypes = null,
    int? Count = null,
    string? LastBarcode = null);

/// <summary>Postal bag counters n/p/m/q.</summary>
/// <param name="N">New parcel count.</param>
/// <param name="P">Deferred parcel count.</param>
/// <param name="M">New parcels with errors.</param>
/// <param name="Q">Deferred parcels with errors.</param>
public sealed record BagClosureCountsV1(int N, int P, int M, int Q)
{
    /// <summary>Gets the effective accepted count.</summary>
    public int EffectiveCount => (N - M) + (P - Q);
}

/// <summary>Per-parcel error returned by CloseBag.</summary>
/// <param name="Barcode">Parcel barcode.</param>
/// <param name="ErrorCode">Stable upstream/application error.</param>
/// <param name="Description">Safe description.</param>
/// <param name="Category">Error category.</param>
public sealed record BagParcelErrorV1(
    string Barcode,
    string ErrorCode,
    string Description,
    string Category);

/// <summary>CloseBag response.</summary>
/// <param name="Status">Success for all non-error result types, otherwise Error.</param>
/// <param name="ResultType">Detailed closure result.</param>
/// <param name="DestinationCenterCode">Echoed request destination.</param>
/// <param name="BagBarcode">Upstream-issued barcode only when completed.</param>
/// <param name="BagLabel">Base64 label only when completed.</param>
/// <param name="Counts">Required for every result except Error.</param>
/// <param name="Errors">Required whenever m or q is non-zero.</param>
/// <param name="ErrorMessage">General error description.</param>
/// <param name="OperationIdIsBestEffort">True when correlationId was used because operationId was omitted.</param>
public sealed record CloseBagResponseV1(
    DeviceStatusCodeV1 Status,
    BagCloseResultTypeV1 ResultType,
    string DestinationCenterCode,
    string? BagBarcode = null,
    string? BagLabel = null,
    BagClosureCountsV1? Counts = null,
    IReadOnlyList<BagParcelErrorV1>? Errors = null,
    string? ErrorMessage = null,
    bool? OperationIdIsBestEffort = null);

/// <summary>CloseDispatch request.</summary>
/// <param name="DestinationCenterCode">Required destination.</param>
public sealed record CloseDispatchRequestV1(string DestinationCenterCode);

/// <summary>Problem parcel reported without preventing dispatch closure.</summary>
/// <param name="Barcode">Parcel barcode.</param>
/// <param name="ErrorCode">Stable problem code.</param>
/// <param name="Description">Safe description.</param>
/// <param name="BagId">Related bag identity.</param>
public sealed record DispatchProblemParcelV1(
    string Barcode,
    string ErrorCode,
    string Description,
    string BagId);

/// <summary>CloseDispatch response. V1 deliberately has no queued/pending result.</summary>
/// <param name="Status">Success or Error.</param>
/// <param name="ResultType">Completed or Error.</param>
/// <param name="DestinationCenterCode">Echoed request destination.</param>
/// <param name="DispatchId">Upstream-issued identity on completion.</param>
/// <param name="BagCount">Bags created by this device.</param>
/// <param name="TotalParcels">Parcels associated with this device.</param>
/// <param name="ProblemParcels">Informational problems that did not stop closure.</param>
/// <param name="ErrorMessage">General error.</param>
/// <param name="OperationIdIsBestEffort">True when correlationId was used because operationId was omitted.</param>
public sealed record CloseDispatchResponseV1(
    DeviceStatusCodeV1 Status,
    DispatchCloseResultTypeV1 ResultType,
    string DestinationCenterCode,
    string? DispatchId = null,
    int? BagCount = null,
    int? TotalParcels = null,
    IReadOnlyList<DispatchProblemParcelV1>? ProblemParcels = null,
    string? ErrorMessage = null,
    bool? OperationIdIsBestEffort = null);
