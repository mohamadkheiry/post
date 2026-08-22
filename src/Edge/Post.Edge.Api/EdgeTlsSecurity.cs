using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Post.Edge.Api;

public sealed class EdgeTlsOptions
{
    public const string SectionName = "Edge:TransportTls";

    public bool RequireTls { get; set; }
}

public sealed record EdgeTlsPolicy(bool RequiresTls);

public static class EdgeTlsConfiguration
{
    public static EdgeTlsPolicy Validate(
        IConfiguration configuration,
        IHostEnvironment environment,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var configured = configuration.GetSection(EdgeTlsOptions.SectionName).Get<EdgeTlsOptions>()
                         ?? new EdgeTlsOptions();
        var requiresTls = environment.IsProduction() || configured.RequireTls;
        if (!environment.IsProduction()) return new EdgeTlsPolicy(requiresTls);

        var certificatePath = configuration["Kestrel:Certificates:Default:Path"];
        if (string.IsNullOrWhiteSpace(certificatePath) || certificatePath.Length > 1_024)
            throw new InvalidOperationException("Production Edge TLS requires a configured server certificate.");

        var resolvedPath = Path.IsPathFullyQualified(certificatePath)
            ? certificatePath
            : Path.GetFullPath(certificatePath, environment.ContentRootPath);
        if (!File.Exists(resolvedPath))
            throw new InvalidOperationException("Production Edge TLS server certificate is unavailable.");

        var keyPath = configuration["Kestrel:Certificates:Default:KeyPath"];
        var password = configuration["Kestrel:Certificates:Default:Password"];
        try
        {
            using var certificate = string.IsNullOrWhiteSpace(keyPath)
                ? X509CertificateLoader.LoadPkcs12FromFile(
                    resolvedPath,
                    password,
                    X509KeyStorageFlags.EphemeralKeySet)
                : X509Certificate2.CreateFromPemFile(
                    resolvedPath,
                    Path.IsPathFullyQualified(keyPath)
                        ? keyPath
                        : Path.GetFullPath(keyPath, environment.ContentRootPath));
            var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
            if (!certificate.HasPrivateKey || timestamp < certificate.NotBefore.ToUniversalTime() ||
                timestamp >= certificate.NotAfter.ToUniversalTime())
            {
                throw new InvalidOperationException(
                    "Production Edge TLS requires a currently valid certificate with a private key.");
            }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Production Edge TLS server certificate is invalid.", exception);
        }

        return new EdgeTlsPolicy(true);
    }
}

internal sealed class EdgeTlsEnforcementMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, EdgeTlsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.RequiresTls &&
            !context.Request.IsHttps &&
            !context.Request.Path.StartsWithSegments("/health"))
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            if (context.Request.Path.StartsWithSegments("/ws/device"))
                context.Response.Headers.Upgrade = "TLS/1.2, HTTP/1.1";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://www.rfc-editor.org/rfc/rfc9110#name-426-upgrade-required",
                title = "TLS is required for this Edge endpoint.",
                status = StatusCodes.Status426UpgradeRequired
            });
            return;
        }

        await next(context);
    }
}
