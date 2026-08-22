namespace Post.Core.Application.Common;

public static class IngestionPayloadLimits
{
    // Matches the persisted integration payload ceiling and Kafka application limit.
    public const int MaximumUtf8Bytes = 1_000_000;

    // Allows a bounded typed envelope around the one-megabyte business payload.
    public const int MaximumRequestBodyBytes = 1_048_576;
}
