using System.Security.Cryptography;
using System.Text;
using Post.Core.Domain.ValueObjects;

namespace Post.Core.Application.Common;

public static class EdgeUploadObjectKey
{
    public static string Create(
        Guid edgeId,
        string exchangeCenterCode,
        Guid readingRecordId,
        Guid eventId)
    {
        _ = new EdgeId(edgeId);
        var center = new ExchangeCenterCode(exchangeCenterCode);
        if (readingRecordId == Guid.Empty || eventId == Guid.Empty)
        {
            throw new CoreValidationException("Reading and upload event identifiers are required.");
        }

        // A digest keeps an arbitrary center code out of the path while retaining a
        // deterministic, center-bound namespace that can be revalidated statelessly.
        var centerDigest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(center.Value)))[..24];
        return string.Concat(
            "edge-uploads/",
            centerDigest,
            "/",
            edgeId.ToString("N"),
            "/",
            readingRecordId.ToString("N"),
            "/",
            eventId.ToString("N"));
    }
}
