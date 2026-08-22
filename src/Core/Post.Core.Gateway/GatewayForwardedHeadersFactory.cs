using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Post.Core.Gateway;

public static class GatewayForwardedHeadersFactory
{
    public static ForwardedHeadersOptions Create(GatewaySecurityOptions security)
    {
        ArgumentNullException.ThrowIfNull(security);
        if (!security.ForwardedHeadersEnabled)
        {
            throw new InvalidOperationException("Forwarded headers are not enabled.");
        }

        if (security.ForwardedHeadersForwardLimit is <= 0 or > 5)
        {
            throw new InvalidOperationException("Forwarded header limit must be between 1 and 5.");
        }

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = security.ForwardedHeadersForwardLimit,
            RequireHeaderSymmetry = security.ForwardedHeadersRequireHeaderSymmetry
        };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();

        foreach (var value in security.ForwardedHeadersKnownProxies)
        {
            if (!IPAddress.TryParse(value, out var address))
            {
                throw new InvalidOperationException($"Trusted proxy IP '{value}' is invalid.");
            }

            options.KnownProxies.Add(address);
        }

        foreach (var value in security.ForwardedHeadersKnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network))
            {
                throw new InvalidOperationException($"Trusted proxy network '{value}' is invalid.");
            }

            options.KnownIPNetworks.Add(network);
        }

        if (options.KnownProxies.Count == 0 && options.KnownIPNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                "Forwarded headers require at least one explicitly trusted proxy IP or CIDR network.");
        }

        return options;
    }
}
