using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Post.Edge.Api;

public static class EdgeForwardedHeaders
{
    public static ForwardedHeadersOptions Create(EdgeTrustedProxyOptions settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
            throw new InvalidOperationException("Trusted Edge proxy forwarding is not enabled.");
        if (settings.ForwardLimit is <= 0 or > 5)
            throw new InvalidOperationException("The trusted Edge proxy forward limit is invalid.");

        var result = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = settings.ForwardLimit,
            RequireHeaderSymmetry = settings.RequireHeaderSymmetry
        };
        result.KnownProxies.Clear();
        result.KnownIPNetworks.Clear();
        foreach (var value in settings.KnownProxies)
        {
            if (!IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException("A trusted Edge proxy address is invalid.");
            result.KnownProxies.Add(address);
        }
        foreach (var value in settings.KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network))
                throw new InvalidOperationException("A trusted Edge proxy network is invalid.");
            result.KnownIPNetworks.Add(network);
        }
        if (result.KnownProxies.Count == 0 && result.KnownIPNetworks.Count == 0)
            throw new InvalidOperationException(
                "Forwarded Edge device addresses require an explicitly trusted proxy or network.");
        return result;
    }
}
