using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Options;
using Post.BuildingBlocks.Serialization;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Integrations.Core;

public sealed class CoreConfigurationSource(
    HttpClient httpClient,
    ICoreAccessTokenProvider tokens,
    IOptions<CoreApiOptions> options) : IConfigurationSource
{
    private static readonly JsonSerializerOptions JsonOptions = PostJsonDefaults.Create(strict: true);

    public async Task<EdgeConfigurationDto?> FetchAsync(long? currentVersion, CancellationToken cancellationToken)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.Value.ExchangeCenterCode))
            query.Add($"exchangeCenterCode={Uri.EscapeDataString(options.Value.ExchangeCenterCode)}");
        if (currentVersion is not null) query.Add($"currentVersion={currentVersion.Value}");
        var path = query.Count == 0
            ? options.Value.BootstrapPath
            : $"{options.Value.BootstrapPath}?{string.Join('&', query)}";
        using var response = await CoreAuthenticatedSender.SendAsync(
            httpClient,
            tokens,
            () => new HttpRequestMessage(HttpMethod.Get, path),
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        response.EnsureSuccessStatusCode();
        var payload = await BoundedHttpResponse.ReadJsonAsync<BootstrapResponse>(
                          response.Content,
                          options.Value.MaximumResponseBytes,
                          JsonOptions,
                          cancellationToken)
                      ?? throw new InvalidOperationException("Core bootstrap response was empty.");
        return new EdgeConfigurationDto(
            payload.ExchangeCenterCode,
            payload.Version,
            payload.ShortReadingThreshold,
            payload.ReturnThreshold,
            payload.Devices.Select(x => new DeviceConfigurationDto(
                x.DeviceId.ToString("D"),
                x.LogicalCode,
                IsActive(x.ActivationStatus),
                x.SecretAlgorithm,
                x.SecretIterations,
                x.SecretSalt,
                x.SecretHash,
                x.PreviousSecretAlgorithm,
                x.PreviousSecretIterations,
                x.PreviousSecretSalt,
                x.PreviousSecretHash,
                x.PreviousSecretValidUntil)).ToArray(),
            payload.PostalCodes,
            payload.AutoDispatchEnabled,
            payload.MaximumOpenBagMinutes <= 0 ? 60 : payload.MaximumOpenBagMinutes);
    }

    private static bool IsActive(JsonElement activationStatus) => activationStatus.ValueKind switch
    {
        JsonValueKind.String => string.Equals(
            activationStatus.GetString(), "Active", StringComparison.OrdinalIgnoreCase),
        JsonValueKind.Number => activationStatus.TryGetInt32(out var numeric) && numeric == 0,
        _ => false
    };

    private sealed record BootstrapResponse(
        string ExchangeCenterCode,
        long Version,
        DateTimeOffset GeneratedAt,
        BootstrapDevice[] Devices,
        string[] PostalCodes,
        TimeSpan ShortReadingThreshold,
        TimeSpan ReturnThreshold,
        decimal WeightToleranceKg,
        decimal DimensionToleranceCm,
        bool AutoDispatchEnabled,
        int MaximumOpenBagMinutes);

    private sealed record BootstrapDevice(
        Guid DeviceId,
        string LogicalCode,
        string Name,
        string DeviceType,
        JsonElement ActivationStatus,
        string SecretAlgorithm,
        int SecretIterations,
        string SecretSalt,
        string SecretHash,
        string? PreviousSecretAlgorithm,
        int? PreviousSecretIterations,
        string? PreviousSecretSalt,
        string? PreviousSecretHash,
        DateTimeOffset? PreviousSecretValidUntil);
}
