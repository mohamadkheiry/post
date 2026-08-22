using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Authentication;
using Post.ApiSupport;
using Post.BuildingBlocks.Serialization;
using Post.Edge.Api;
using Post.Edge.Application;
using Post.Edge.Infrastructure;
using Post.Edge.Infrastructure.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
});
builder.AddPostApiDefaults("Post.Edge.Api");
builder.Services.AddEdgeApplication();
builder.Services.AddEdgeInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(EdgeTlsConfiguration.Validate(
    builder.Configuration,
    builder.Environment));
builder.Services.AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, LocalAdministratorAuthenticationHandler>(
        LocalAdministratorAuthentication.Scheme,
        _ => { });
builder.Services.AddAuthorizationBuilder().AddPolicy(
    LocalAdministratorAuthentication.Policy,
    policy => policy
        .AddAuthenticationSchemes(LocalAdministratorAuthentication.Scheme)
        .RequireAuthenticatedUser()
        .RequireRole("EdgeAdministrator"));
var transportOptions = builder.Configuration.GetSection(EdgeDeviceTransportOptions.SectionName)
                           .Get<EdgeDeviceTransportOptions>()
                       ?? new EdgeDeviceTransportOptions();
transportOptions.Validate();
var trustedProxyOptions = builder.Configuration.GetSection(EdgeTrustedProxyOptions.SectionName)
                              .Get<EdgeTrustedProxyOptions>()
                          ?? new EdgeTrustedProxyOptions();
var forwardedHeaders = trustedProxyOptions.Enabled
    ? EdgeForwardedHeaders.Create(trustedProxyOptions)
    : null;
builder.Services.Configure<EdgeDeviceTransportOptions>(
    builder.Configuration.GetSection(EdgeDeviceTransportOptions.SectionName));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("device-connections", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unavailable",
        _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = transportOptions.ConnectionPermitLimit,
            QueueLimit = 0,
            Window = transportOptions.ConnectionRateWindow
        }));
    var administrator = builder.Configuration.GetSection(EdgeLocalAdministratorOptions.SectionName)
                            .Get<EdgeLocalAdministratorOptions>()
                        ?? new EdgeLocalAdministratorOptions();
    options.AddPolicy("edge-admin-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unavailable",
        _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = administrator.LoginPermitLimit,
            QueueLimit = 0,
            Window = administrator.LoginRateWindow
        }));
});
builder.Services
    .AddSignalR(options =>
    {
        options.EnableDetailedErrors = false;
        options.MaximumParallelInvocationsPerClient = 1;
        options.MaximumReceiveMessageSize = transportOptions.MaximumReceiveMessageSizeBytes;
        options.HandshakeTimeout = transportOptions.HandshakeTimeout;
        options.ClientTimeoutInterval = transportOptions.ClientTimeoutInterval;
        options.KeepAliveInterval = transportOptions.KeepAliveInterval;
    })
    .AddJsonProtocol(options => PostJsonDefaults.Apply(options.PayloadSerializerOptions));
builder.Services.AddHostedService<EdgeConfigurationBootstrapHostedService>();

EdgeProductionConfiguration.Validate(builder.Configuration, builder.Environment);

var app = builder.Build();
if (forwardedHeaders is not null) app.UseForwardedHeaders(forwardedHeaders);
app.UseMiddleware<EdgeAdministratorRequestBodyLimitMiddleware>();
app.UsePostApiDefaults();
app.UseMiddleware<EdgeTlsEnforcementMiddleware>();
app.UseRateLimiter();
app.MapHub<DeviceHub>("/ws/device", options =>
{
    options.Transports = HttpTransportType.WebSockets;
}).RequireRateLimiting("device-connections").AllowAnonymous();
app.MapEdgeEndpoints();
app.Run();

public partial class Program;
