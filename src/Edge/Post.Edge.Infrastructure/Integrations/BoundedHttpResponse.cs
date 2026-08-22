using System.Buffers;
using System.Text.Json;

namespace Post.Edge.Infrastructure.Integrations;

internal static class BoundedHttpResponse
{
    internal const int HardMaximumResponseBytes = 33_554_432;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal static async Task<T?> ReadJsonAsync<T>(
        HttpContent content,
        int maximumBytes,
        JsonSerializerOptions? jsonOptions,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(content, maximumBytes, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, jsonOptions ?? WebJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The external service returned malformed JSON.");
        }
    }

    internal static async Task<JsonDocument> ReadJsonDocumentAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBytesAsync(content, maximumBytes, cancellationToken);
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The external service returned malformed JSON.");
        }
    }

    internal static async Task<byte[]> ReadBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateLimit(maximumBytes);
        if (content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new InvalidOperationException("The external service response exceeded the configured size limit.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(Math.Min(maximumBytes, 81_920));
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0) return destination.ToArray();
                if (destination.Length + read > maximumBytes)
                    throw new InvalidOperationException(
                        "The external service response exceeded the configured size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static void ValidateLimit(int maximumBytes)
    {
        if (maximumBytes <= 0 || maximumBytes > HardMaximumResponseBytes)
            throw new InvalidOperationException("External response limit must be positive and at most 32 MiB.");
    }
}
