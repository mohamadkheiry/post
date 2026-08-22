using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Contracts;
using Post.Edge.Application.Services;

namespace Post.Edge.Api;

internal static class LocalAdministratorAuthentication
{
    internal const string Scheme = "EdgeLocalAdministrator";
    internal const string Policy = "EdgeLocalAdministrator";

    internal static string? ReadBearerToken(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var authorization = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorization[prefix.Length..].Trim();
        return token.Length is > 0 and <= LocalAdministratorInputLimits.MaximumTokenCharacters ? token : null;
    }
}

internal sealed class LocalAdministratorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ILocalAdministratorService administrators)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = LocalAdministratorAuthentication.ReadBearerToken(Request);
        if (token is null) return AuthenticateResult.NoResult();
        var userName = await administrators.AuthenticateSessionAsync(token, Context.RequestAborted);
        if (userName is null)
            return AuthenticateResult.Fail("The local administrator session is invalid or expired.");
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userName),
            new Claim(ClaimTypes.Name, userName),
            new Claim("sub", userName),
            new Claim(ClaimTypes.Role, "EdgeAdministrator")
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
