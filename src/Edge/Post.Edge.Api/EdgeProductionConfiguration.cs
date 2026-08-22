using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Api;

public static class EdgeProductionConfiguration
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.IsProduction()) return;
        if (!configuration.GetValue(
                $"{EdgeLocalAdministratorOptions.SectionName}:AuthenticationEnabled",
                true))
        {
            throw new InvalidOperationException(
                "Production Edge API requires local-administrator authentication.");
        }

        var profile = configuration["Edge:Integrations:Profile"]
                      ?? configuration["Edge:Integrations:Mode"];
        if (!string.Equals(profile, "Connected", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(profile, "Real", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Production Edge API requires the Connected integration profile.");
        }
    }
}
