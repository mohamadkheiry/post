using System.Net;
using System.Net.Http.Headers;

namespace Post.Edge.Infrastructure.Integrations.Core;

internal static class CoreAuthenticatedSender
{
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        ICoreAccessTokenProvider tokens,
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(requestFactory);

        var token = await tokens.GetAsync(cancellationToken);
        using var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        response.Dispose();
        var refreshed = await tokens.RefreshAfterUnauthorizedAsync(token, cancellationToken);
        using var retry = requestFactory();
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        return await httpClient.SendAsync(
            retry,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }
}
