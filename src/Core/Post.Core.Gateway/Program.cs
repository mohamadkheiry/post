using System.Buffers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Post.Core.Gateway;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});
builder.Logging.Configure(options =>
    options.ActivityTrackingOptions =
        ActivityTrackingOptions.TraceId |
        ActivityTrackingOptions.SpanId |
        ActivityTrackingOptions.ParentId);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GatewayExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = static (context, _) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return ValueTask.CompletedTask;
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var configuredSecurity = ReadSecurity(context.RequestServices.GetRequiredService<IConfiguration>());
        var authenticatedSubject = configuredSecurity.AuthenticationEnabled
            ? context.User.FindFirstValue(JwtRegisteredClaimNames.Sub)
              ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;
        var partition = authenticatedSubject ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetTokenBucketLimiter(partition, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = configuredSecurity.RateLimitPermitLimit,
            TokensPerPeriod = configuredSecurity.RateLimitTokensPerPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(configuredSecurity.RateLimitReplenishmentSeconds),
            QueueLimit = configuredSecurity.RateLimitQueueLimit,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    });
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var configuredSecurity = ReadSecurity(builder.Configuration);
        // WebApplication automatically inserts authentication middleware when a scheme is
        // registered. Auth-off Development/Testing therefore needs a structurally valid,
        // explicitly non-secret verification key even though no authorization policy uses it.
        const string disabledAuthenticationKey = "auth-disabled-non-secret-validation-key";
        var signingKey = configuredSecurity.AuthenticationEnabled
            ? configuredSecurity.SigningKey
            : disabledAuthenticationKey;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = configuredSecurity.RequireHttpsMetadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = configuredSecurity.Issuer,
            ValidateAudience = true,
            ValidAudience = configuredSecurity.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(configuredSecurity.ClockSkewSeconds),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = "role"
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();
var security = ReadSecurity(app.Configuration);
ValidateSecurityOptions(security, app.Environment);
if (security.ForwardedHeadersEnabled)
{
    app.UseForwardedHeaders(GatewayForwardedHeadersFactory.Create(security));
}

if (security.AuthenticationEnabled)
{
    app.Services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.FallbackPolicy =
        new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
}

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (!IsJsonWrite(context.Request))
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    await InvokeBoundedJsonBodyAsync(
        context,
        next,
        security.JsonRequestBodyMaximumBytes).ConfigureAwait(false);
});
app.Use(async (context, next) =>
{
    const string correlationHeader = "X-Correlation-ID";
    var supplied = context.Request.Headers[correlationHeader].FirstOrDefault();
    var correlationId = Guid.TryParse(supplied, out var parsed) ? parsed : Guid.NewGuid();
    context.TraceIdentifier = correlationId.ToString("D");
    context.Request.Headers[correlationHeader] = context.TraceIdentifier;
    var requestLogger = context.RequestServices
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Post.Core.Gateway.Request");
    using var correlationScope = requestLogger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["CorrelationId"] = context.TraceIdentifier
    });
    context.Response.OnStarting(() =>
    {
        context.Response.Headers[correlationHeader] = context.TraceIdentifier;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    await next(context);
});

app.UseHttpMetrics(options => options.ReduceStatusCodeCardinality());

if (security.AuthenticationEnabled)
{
    app.UseAuthentication();
}

app.Use(async (context, next) =>
{
    var edgeId = context.User.FindFirstValue("device_id");
    var center = context.User.FindAll("allowed_center")
        .Select(claim => claim.Value?.Trim())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    if (!Guid.TryParse(edgeId, out var parsedEdgeId) || parsedEdgeId == Guid.Empty)
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    var values = new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["EdgeId"] = parsedEdgeId.ToString("D")
    };
    if (center.Length == 1 && center[0]!.Length <= 32 && !center[0]!.Any(char.IsControl))
    {
        values["ExchangeCenterCode"] = center[0];
    }

    var logger = context.RequestServices
        .GetRequiredService<ILoggerFactory>()
        .CreateLogger("Post.Core.Gateway.AuthenticatedRequest");
    using var scope = logger.BeginScope(values);
    await next(context).ConfigureAwait(false);
});

app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new
{
    service = "Post.Core.Gateway",
    status = "ready",
    utc = DateTimeOffset.UtcNow
})).AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();
app.MapMetrics("/metrics").AllowAnonymous();

app.MapReverseProxy();

app.Run();

static GatewaySecurityOptions ReadSecurity(IConfiguration configuration) =>
    configuration.GetSection(GatewaySecurityOptions.SectionName).Get<GatewaySecurityOptions>()
    ?? new GatewaySecurityOptions();

static void ValidateSecurityOptions(GatewaySecurityOptions security, IHostEnvironment environment)
{
    if (!security.AuthenticationEnabled && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
    {
        throw new InvalidOperationException("Gateway authentication may be disabled only in Development or Testing.");
    }

    if (security.AuthenticationEnabled &&
        (string.IsNullOrWhiteSpace(security.SigningKey) || Encoding.UTF8.GetByteCount(security.SigningKey) < 32))
    {
        throw new InvalidOperationException("Auth:SigningKey must contain at least 32 UTF-8 bytes.");
    }

    if (security.AuthenticationEnabled &&
        (string.IsNullOrWhiteSpace(security.Issuer) || string.IsNullOrWhiteSpace(security.Audience)))
    {
        throw new InvalidOperationException("Auth:Issuer and Auth:Audience are required when gateway authentication is enabled.");
    }

    if (security.ClockSkewSeconds is < 0 or > 300 ||
        security.RateLimitPermitLimit <= 0 ||
        security.RateLimitTokensPerPeriod <= 0 ||
        security.RateLimitReplenishmentSeconds <= 0 ||
        security.RateLimitQueueLimit < 0 ||
        security.JsonRequestBodyMaximumBytes is < 8 * 1024 or > 16 * 1024 * 1024)
    {
        throw new InvalidOperationException(
            "Gateway clock-skew, rate-limit, or JSON body-limit settings are outside their safe ranges.");
    }
}

static bool IsJsonWrite(HttpRequest request)
{
    if (!HttpMethods.IsPost(request.Method) &&
        !HttpMethods.IsPut(request.Method) &&
        !HttpMethods.IsPatch(request.Method))
    {
        return false;
    }

    var mediaType = request.ContentType?.Split(';', 2, StringSplitOptions.TrimEntries)[0];
    return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
           (mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false);
}

static async Task InvokeBoundedJsonBodyAsync(
    HttpContext context,
    RequestDelegate next,
    long maximumBytes)
{
    if (context.Request.ContentLength > maximumBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    var requestSizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (requestSizeFeature is { IsReadOnly: false })
    {
        requestSizeFeature.MaxRequestBodySize = maximumBytes;
    }

    if (context.Request.ContentLength is not null)
    {
        await next(context).ConfigureAwait(false);
        return;
    }

    var originalBody = context.Request.Body;
    await using var boundedBody = new MemoryStream();
    var buffer = ArrayPool<byte>.Shared.Rent(16_384);
    try
    {
        while (true)
        {
            var read = await originalBody.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (boundedBody.Length + read > maximumBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            await boundedBody.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
        }

        boundedBody.Position = 0;
        context.Request.Body = boundedBody;
        await next(context).ConfigureAwait(false);
    }
    finally
    {
        context.Request.Body = originalBody;
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

/// <summary>Marker for component testing with WebApplicationFactory.</summary>
public partial class Program;
