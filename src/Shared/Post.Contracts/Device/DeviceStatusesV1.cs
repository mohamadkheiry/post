using System.Text.Json.Serialization;

namespace Post.Contracts.V1.Device;

/// <summary>Numeric status code defined by the device protocol's comprehensive status table.</summary>
public enum DeviceStatusCodeV1
{
    /// <summary>Operation succeeded.</summary>
    Success = 0,
    /// <summary>The exchange completed with a pending operational outcome.</summary>
    Pending = 1,
    /// <summary>Operation failed.</summary>
    Error = 2,
    /// <summary>Parcel was classified as returned.</summary>
    Returned = 3,
    /// <summary>Parcel was refused and must return to origin.</summary>
    Refused = 4
}

/// <summary>Numeric status code specific to destination assignment.</summary>
public enum DestinationAssignmentStatusCodeV1
{
    /// <summary>Destination was confirmed.</summary>
    Confirmed = 1,
    /// <summary>Destination assignment failed.</summary>
    Error = 2
}

/// <summary>Detailed device-visible outcome of bag closure.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BagCloseResultTypeV1>))]
public enum BagCloseResultTypeV1
{
    /// <summary>Bag and label were created.</summary>
    [JsonStringEnumMemberName("Completed")]
    Completed,
    /// <summary>No parcel matched the selection criteria.</summary>
    [JsonStringEnumMemberName("NoEligibleParcels")]
    NoEligibleParcels,
    /// <summary>Every selected parcel failed export registration.</summary>
    [JsonStringEnumMemberName("AllParcelsFailed")]
    AllParcelsFailed,
    /// <summary>The physical bag closed while barcode and label remain pending.</summary>
    [JsonStringEnumMemberName("LabelPending")]
    LabelPending,
    /// <summary>A general error prevented bag closure.</summary>
    [JsonStringEnumMemberName("Error")]
    Error
}

/// <summary>Detailed device-visible outcome of dispatch closure.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DispatchCloseResultTypeV1>))]
public enum DispatchCloseResultTypeV1
{
    /// <summary>Dispatch closed immediately.</summary>
    [JsonStringEnumMemberName("Completed")]
    Completed,
    /// <summary>Dispatch did not close; the device may issue a new operation later.</summary>
    [JsonStringEnumMemberName("Error")]
    Error
}
