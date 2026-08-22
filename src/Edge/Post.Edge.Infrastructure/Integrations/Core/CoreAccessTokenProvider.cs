using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Resilience;

namespace Post.Edge.Infrastructure.Integrations.Core;

public interface ICoreAccessTokenProvider
{
    Task<string> GetAsync(CancellationToken cancellationToken);
    Task<string> RefreshAfterUnauthorizedAsync(string rejectedToken, CancellationToken cancellationToken);
}

public sealed class CoreAccessTokenProvider(
    HttpClient httpClient,
    IOptions<CoreApiOptions> options,
    IClock clock) : ICoreAccessTokenProvider, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TokenSnapshot? _current;

    public Task<string> GetAsync(CancellationToken cancellationToken) =>
        GetCoreAsync(null, cancellationToken);

    public Task<string> RefreshAfterUnauthorizedAsync(
        string rejectedToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rejectedToken);
        return GetCoreAsync(rejectedToken, cancellationToken);
    }

    public void Dispose() => _gate.Dispose();

    private async Task<string> GetCoreAsync(string? rejectedToken, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.UseFixedBearerToken)
        {
            if (string.IsNullOrWhiteSpace(settings.BearerToken))
                throw new InvalidOperationException("The explicit fixed Core bearer-token override is empty.");
            return settings.BearerToken;
        }

        var current = _current;
        if (CanUse(current, rejectedToken, settings.TokenRefreshSkew)) return current!.Value;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            current = _current;
            if (CanUse(current, rejectedToken, settings.TokenRefreshSkew)) return current!.Value;
            if (string.IsNullOrWhiteSpace(settings.LogicalDeviceCode) ||
                string.IsNullOrWhiteSpace(settings.DeviceSecret))
                throw new InvalidOperationException("Core rotating device credentials are not configured.");

            using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenPath)
            {
                Content = JsonContent.Create(new
                {
                    logicalDeviceCode = settings.LogicalDeviceCode,
                    deviceSecret = settings.DeviceSecret
                })
            };
            EdgeHttpResilience.MarkReplaySafe(request);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var payload = await BoundedHttpResponse.ReadJsonAsync<TokenResponse>(
                              response.Content,
                              settings.MaximumResponseBytes,
                              null,
                              cancellationToken)
                          ?? throw new InvalidOperationException("Core access-token response was empty.");
            var token = string.IsNullOrWhiteSpace(payload.AccessToken) ? payload.Token : payload.AccessToken;
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Core access-token response omitted the token.");
            var expiresAt = payload.ExpiresAt ?? clock.UtcNow.AddSeconds(
                payload.ExpiresIn is > 0
                    ? payload.ExpiresIn.Value
                    : settings.DefaultTokenLifetime.TotalSeconds);
            if (expiresAt <= clock.UtcNow)
                throw new InvalidOperationException("Core access-token response contained an expired token.");
            _current = new TokenSnapshot(token, expiresAt);
            return token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool CanUse(TokenSnapshot? snapshot, string? rejectedToken, TimeSpan skew) =>
        snapshot is not null &&
        !string.Equals(snapshot.Value, rejectedToken, StringComparison.Ordinal) &&
        clock.UtcNow < snapshot.ExpiresAt - skew;

    private sealed record TokenSnapshot(string Value, DateTimeOffset ExpiresAt);

    private sealed record TokenResponse(
        [property: JsonPropertyName("accessToken")] string? AccessToken,
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("expiresIn")] int? ExpiresIn,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);
}
