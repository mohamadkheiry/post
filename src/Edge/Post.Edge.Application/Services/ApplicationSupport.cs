using System.Text.Json;
using System.Security.Cryptography;
using Post.BuildingBlocks.Serialization;
using Post.Edge.Application.Ports;

namespace Post.Edge.Application.Services;

internal static class ApplicationSupport
{
    private static readonly JsonSerializerOptions JsonOptions = PostJsonDefaults.Create();

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static T? TryFromJson<T>(string? value) where T : class
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(value, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public static string RequestHash<T>(T value)
    {
        using var algorithm = SHA256.Create();
        using (var stream = new CryptoStream(Stream.Null, algorithm, CryptoStreamMode.Write, leaveOpen: true))
        {
            JsonSerializer.Serialize(stream, value, JsonOptions);
            stream.FlushFinalBlock();
        }
        return Convert.ToHexString(algorithm.Hash
            ?? throw new InvalidOperationException("Request hashing did not produce a digest."));
    }

    public static OutboxMessage Outbox(
        IClock clock,
        string target,
        string messageType,
        object payload,
        string idempotencyKey) => new(
        Guid.NewGuid(),
        target,
        messageType,
        ToJson(payload),
        idempotencyKey,
        clock.UtcNow,
        clock.UtcNow);

    public static OutboxMessage OperationalResult(
        IClock clock,
        string operationKind,
        Guid operationId,
        string? parcelBarcode,
        string callResult,
        bool success,
        string? errorCode,
        string? errorMessage,
        DateTimeOffset calledAt,
        int attempts,
        string finalStatus) => Outbox(
            clock,
            "Core",
            "OperationalResultCaptured",
            new CoreOperationalResultMessage(
                operationId.ToString("D"),
                parcelBarcode,
                callResult,
                success,
                errorCode,
                errorMessage,
                calledAt,
                clock.UtcNow,
                attempts,
                finalStatus),
            $"operational:{operationKind}:{operationId:N}:{attempts}");
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
