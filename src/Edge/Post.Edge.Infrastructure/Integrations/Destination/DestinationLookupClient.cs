using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Resilience;

namespace Post.Edge.Infrastructure.Integrations.Destination;

public sealed class DestinationTokenCache(
    HttpClient httpClient,
    IOptions<DestinationApiOptions> options,
    IClock clock) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (_token is not null && clock.UtcNow < _expiresAt - settings.RefreshSkew) return _token;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && clock.UtcNow < _expiresAt - settings.RefreshSkew) return _token;
            if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.Password))
            {
                throw new InvalidOperationException("Destination API credentials are not configured.");
            }

            using var form = new MultipartFormDataContent
            {
                { new StringContent("password"), "grant_type" },
                { new StringContent(settings.Username), "username" },
                { new StringContent(settings.Password), "password" },
                { new StringContent(string.Empty), "refresh_token" },
                { new StringContent(string.Empty), "scope" },
                { new StringContent(string.Empty), "client_id" },
                { new StringContent(string.Empty), "client_secret" }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenPath) { Content = form };
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
                          ?? throw new InvalidOperationException("Destination token response was empty.");
            if (string.IsNullOrWhiteSpace(payload.AccessToken))
            {
                throw new InvalidOperationException("Destination token response omitted access_token.");
            }

            _token = payload.AccessToken;
            _expiresAt = clock.UtcNow.AddSeconds(payload.ExpiresIn is > 0
                ? payload.ExpiresIn.Value
                : settings.TokenLifetime.TotalSeconds);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int? ExpiresIn);
}

public sealed class HttpDestinationLookupClient(
    HttpClient httpClient,
    DestinationTokenCache tokenCache,
    IOptions<DestinationApiOptions> options) : IDestinationLookupClient
{
    public async Task<string?> ResolveAsync(string fourteenDigitBarcode, CancellationToken cancellationToken)
    {
        if (fourteenDigitBarcode.Length != 14 || fourteenDigitBarcode.Any(x => x is < '0' or > '9'))
        {
            throw new ArgumentException("Destination lookup requires a 14-digit barcode.", nameof(fourteenDigitBarcode));
        }

        var token = await tokenCache.GetAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.ParcelInfoPath)
        {
            Content = JsonContent.Create(new { ParcelCode = fourteenDigitBarcode },
                mediaType: new MediaTypeHeaderValue("application/json-patch+json"))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        EdgeHttpResilience.MarkReplaySafe(request);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = await BoundedHttpResponse.ReadJsonDocumentAsync(
            response.Content,
            options.Value.MaximumResponseBytes,
            cancellationToken);
        if (!TryFindProperty(json.RootElement, "ToCityID", out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number.ToString(CultureInfo.InvariantCulture).PadLeft(5, '0'),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => value.GetString()!.Trim().PadLeft(5, '0'),
            _ => null
        };
    }

    private static bool TryFindProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
                if (TryFindProperty(property.Value, name, out value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindProperty(item, name, out value)) return true;
            }
        }

        value = default;
        return false;
    }
}
