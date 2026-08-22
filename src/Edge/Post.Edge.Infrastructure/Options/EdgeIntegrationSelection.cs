using Microsoft.Extensions.Configuration;

namespace Post.Edge.Infrastructure.Options;

internal enum EdgeAdapterMode
{
    Mock,
    Real
}

internal sealed record EdgeIntegrationSelection(
    string Profile,
    EdgeAdapterMode Destination,
    EdgeAdapterMode Postal,
    EdgeAdapterMode Core,
    bool IsDevelopmentProfile,
    bool IsStrictConnected)
{
    internal static EdgeIntegrationSelection Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var profile = FirstConfigured(
            configuration[$"{EdgeIntegrationOptions.SectionName}:Profile"],
            configuration[$"{EdgeIntegrationOptions.SectionName}:Mode"])
            ?? "LocalConnected";

        var normalizedProfile = profile.Trim();
        (EdgeAdapterMode? Destination, EdgeAdapterMode? Postal, EdgeAdapterMode? Core,
            bool IsDevelopment, bool IsStrictConnected) defaults = normalizedProfile.ToUpperInvariant() switch
            {
                "MOCK" => (EdgeAdapterMode.Mock, EdgeAdapterMode.Mock, EdgeAdapterMode.Mock, true, false),
                "LOCALCONNECTED" or "LOCAL-CONNECTED" or "HYBRID" =>
                    (EdgeAdapterMode.Real, EdgeAdapterMode.Mock, EdgeAdapterMode.Mock, true, false),
                "CORECONNECTED" or "CORE-CONNECTED" =>
                    (EdgeAdapterMode.Mock, EdgeAdapterMode.Mock, EdgeAdapterMode.Real, true, false),
                "CONNECTED" or "REAL" => (EdgeAdapterMode.Real, EdgeAdapterMode.Real, EdgeAdapterMode.Real, false, true),
                "CUSTOM" => ((EdgeAdapterMode?)null, null, null, false, false),
                _ => throw new InvalidOperationException(
                    "Edge integration profile must be Mock, LocalConnected, CoreConnected, Connected, or Custom.")
            };

        var destination = ResolveComponent(configuration, "Destination", "DestinationApi", defaults.Destination);
        var postal = ResolveComponent(configuration, "Postal", "PostalApi", defaults.Postal);
        var core = ResolveComponent(configuration, "Core", "CoreApi", defaults.Core);
        if (defaults.IsStrictConnected &&
            (destination != EdgeAdapterMode.Real || postal != EdgeAdapterMode.Real || core != EdgeAdapterMode.Real))
        {
            throw new InvalidOperationException("The Connected integration profile requires every adapter to be Real.");
        }

        return new EdgeIntegrationSelection(
            normalizedProfile,
            destination,
            postal,
            core,
            defaults.IsDevelopment,
            defaults.IsStrictConnected);
    }

    private static EdgeAdapterMode ResolveComponent(
        IConfiguration configuration,
        string component,
        string legacyComponent,
        EdgeAdapterMode? fallback)
    {
        var value = FirstConfigured(
            configuration[$"{EdgeIntegrationOptions.SectionName}:{component}"],
            configuration[$"{EdgeIntegrationOptions.SectionName}:{component}:Mode"],
            configuration[$"{EdgeIntegrationOptions.SectionName}:{component}Mode"],
            configuration[$"{EdgeIntegrationOptions.SectionName}:{legacyComponent}:Mode"]);
        if (value is null)
        {
            return fallback ?? throw new InvalidOperationException(
                $"The Custom integration profile requires an explicit {component} adapter mode.");
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "MOCK" => EdgeAdapterMode.Mock,
            "REAL" or "CONNECTED" => EdgeAdapterMode.Real,
            _ => throw new InvalidOperationException($"The {component} adapter mode must be Mock or Real.")
        };
    }

    private static string? FirstConfigured(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
