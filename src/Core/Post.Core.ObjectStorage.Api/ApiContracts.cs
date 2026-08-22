namespace Post.Core.ObjectStorage.Api;

public sealed record PresignedUrlRequest(
    Guid EventId,
    Guid ReadingRecordId,
    string AttachmentType,
    string ContentType,
    long ContentLength,
    int LifetimeSeconds);

public sealed class ObjectStorageApiMarker;
