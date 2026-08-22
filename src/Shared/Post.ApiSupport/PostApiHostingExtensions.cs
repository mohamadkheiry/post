using System.Buffers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Post.BuildingBlocks.Correlation;
using Post.BuildingBlocks.Serialization;
using Prometheus;

namespace Post.ApiSupport;

public static class PostApiHostingExtensions
{
    public static WebApplicationBuilder AddPostApiDefaults(
        this WebApplicationBuilder builder,
        string serviceName,
        bool supportAuthentication = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        ConfigureJsonLogging(builder.Logging);

        var apiOptions = builder.Configuration.GetSection(PostApiOptions.SectionName).Get<PostApiOptions>()
                         ?? new PostApiOptions();
        if (apiOptions.JsonRequestBodyMaximumBytes is < PostApiOptions.MinimumJsonRequestBodyBytes or
            > PostApiOptions.MaximumJsonRequestBodyBytes)
        {
            throw new InvalidOperationException(
                $"Api:JsonRequestBodyMaximumBytes must be between " +
                $"{PostApiOptions.MinimumJsonRequestBodyBytes} and {PostApiOptions.MaximumJsonRequestBodyBytes} bytes.");
        }

        if (apiOptions.AuthenticationEnabled && !supportAuthentication)
        {
            throw new InvalidOperationException($"{serviceName} does not support bearer authentication.");
        }

        if (!apiOptions.AuthenticationEnabled && !builder.Environment.IsDevelopment() &&
            !builder.Environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException("API authentication may be disabled only in Development or Testing.");
        }

        builder.Services.Configure<PostApiOptions>(builder.Configuration.GetSection(PostApiOptions.SectionName));
        builder.Services.AddSingleton(new PostServiceDescriptor(serviceName));
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();
        builder.Services.AddExceptionHandler<PostExceptionHandler>();
        builder.Services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
            };
        });
        builder.Services.AddHealthChecks();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = serviceName,
                Version = "v1",
                Description = "Post sorting platform Phase 1 API"
            });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "JWT issued by Post.Core.Identity.Api"
            });
        });
        builder.Services.Configure<JsonOptions>(options => PostJsonDefaults.Apply(options.SerializerOptions));

        if (apiOptions.AuthenticationEnabled)
        {
            AddAuthentication(builder);
        }

        builder.Services.AddAuthorization(options =>
        {
            if (apiOptions.AuthenticationEnabled)
            {
                options.AddPolicy(PostAuthorizationPolicies.Edge,
                    policy => policy.RequireAuthenticatedUser().RequireRole("Edge"));
                options.AddPolicy(PostAuthorizationPolicies.Administrator,
                    policy => policy.RequireAuthenticatedUser().RequireRole(
                        "SystemAdministrator",
                        "GlobalAdministrator",
                        "CenterAdministrator"));
            }
            else
            {
                // The startup guard above confines this explicit bypass to Development/Testing.
                options.AddPolicy(PostAuthorizationPolicies.Edge,
                    policy => policy.RequireAssertion(_ => true));
                options.AddPolicy(PostAuthorizationPolicies.Administrator,
                    policy => policy.RequireAssertion(_ => true));
            }
        });

        return builder;
    }

    public static WebApplication UsePostApiDefaults(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationContextMiddleware>();
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
                app.Configuration.GetValue(
                    "Api:JsonRequestBodyMaximumBytes",
                    2L * 1024 * 1024)).ConfigureAwait(false);
        });
        app.Use(async (context, next) =>
        {
            var requestLogger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Post.Request");
            using var correlationScope = requestLogger.BeginScope(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["CorrelationId"] = context.TraceIdentifier
            });
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.XContentTypeOptions = "nosniff";
                context.Response.Headers.XFrameOptions = "DENY";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                return Task.CompletedTask;
            });
            await next(context).ConfigureAwait(false);
        });

        var options = app.Configuration.GetSection(PostApiOptions.SectionName).Get<PostApiOptions>()
                      ?? new PostApiOptions();
        if (!options.AuthenticationEnabled &&
            app.Configuration.GetValue<bool>("Api:DevelopmentIdentity:Enabled"))
        {
            var claims = ReadDevelopmentIdentityClaims(app.Configuration);
            app.Use(async (context, next) =>
            {
                context.User = new ClaimsPrincipal(
                    new ClaimsIdentity(claims, "DevelopmentIdentity", "sub", "role"));
                await next(context).ConfigureAwait(false);
            });
        }

        if (options.MetricsEnabled)
        {
            app.UseHttpMetrics(httpMetrics =>
            {
                httpMetrics.ReduceStatusCodeCardinality();
            });
        }

        if (options.AuthenticationEnabled)
        {
            app.UseAuthentication();
        }

        app.Use(async (context, next) =>
        {
            var identityScope = AuthenticatedIdentityScope(context.User);
            if (identityScope.Count == 0)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var identityLogger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Post.AuthenticatedRequest");
            using var scope = identityLogger.BeginScope(identityScope);
            await next(context).ConfigureAwait(false);
        });

        app.UseAuthorization();
        if (!app.Environment.IsProduction() || options.SwaggerEnabled)
        {
            app.UseSwagger();
            app.UseSwaggerUI(swagger =>
            {
                swagger.SwaggerEndpoint("/swagger/v1/swagger.json", "v1");
                swagger.DocumentTitle = app.Services.GetRequiredService<PostServiceDescriptor>().Name;
            });
        }

        app.MapGet("/", (PostServiceDescriptor service, TimeProvider timeProvider) => Results.Ok(new
        {
            service = service.Name,
            status = "ready",
            utc = timeProvider.GetUtcNow()
        })).AllowAnonymous().ExcludeFromDescription();
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready")
        }).AllowAnonymous();
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = (context, report) =>
                WriteHealthResponseAsync(context, report, app.Environment.IsProduction())
        }).AllowAnonymous();
        if (options.MetricsEnabled)
        {
            app.MapMetrics("/metrics").AllowAnonymous().ExcludeFromDescription();
        }

        return app;
    }

    private static void ConfigureJsonLogging(ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            options.JsonWriterOptions = new JsonWriterOptions { Indented = false };
        });
        logging.Configure(options =>
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.ParentId);
    }

    private static bool IsJsonWrite(HttpRequest request)
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

    private static async Task InvokeBoundedJsonBodyAsync(
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

    private static Claim[] ReadDevelopmentIdentityClaims(IConfiguration configuration)
    {
        var edgeIdValue = configuration["Api:DevelopmentIdentity:EdgeId"];
        var centerCode = configuration["Api:DevelopmentIdentity:ExchangeCenterCode"];
        if (!Guid.TryParse(edgeIdValue, out var edgeId) || edgeId == Guid.Empty ||
            string.IsNullOrWhiteSpace(centerCode) || centerCode.Trim().Length > 32)
        {
            throw new InvalidOperationException(
                "Enabled development identity requires a non-empty EdgeId and exchange center code.");
        }

        return
        [
            new Claim("sub", edgeId.ToString("D")),
            new Claim("device_id", edgeId.ToString("D")),
            new Claim("allowed_center", centerCode.Trim()),
            new Claim("role", "Edge"),
            new Claim("role", "CenterAdministrator")
        ];
    }

    private static Dictionary<string, object?> AuthenticatedIdentityScope(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (Guid.TryParse(principal.FindFirstValue("device_id"), out var edgeId) && edgeId != Guid.Empty)
        {
            values["EdgeId"] = edgeId.ToString("D");
        }

        var centers = principal.Claims
            .Where(claim => claim.Type is "allowed_center" or "allowed_centers")
            .SelectMany(claim => ParseLogCenters(claim.Value))
            .Where(center => !string.IsNullOrWhiteSpace(center))
            .Select(center => center.Trim())
            .Where(center => center.Length <= 32 && !center.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (centers.Length == 1)
        {
            values["ExchangeCenterCode"] = centers[0];
        }

        return values;
    }

    private static string[] ParseLogCenters(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        if (!value.TrimStart().StartsWith('['))
        {
            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddAuthentication(WebApplicationBuilder builder)
    {
        var jwt = builder.Configuration.GetSection(PostJwtOptions.SectionName).Get<PostJwtOptions>()
                  ?? new PostJwtOptions();
        if (string.IsNullOrWhiteSpace(jwt.SigningKey) || Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
        {
            throw new InvalidOperationException("Auth:SigningKey must contain at least 32 UTF-8 bytes when API authentication is enabled.");
        }

        if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
        {
            throw new InvalidOperationException("Auth:Issuer and Auth:Audience are required when API authentication is enabled.");
        }

        if (jwt.ClockSkewSeconds is < 0 or > 300)
        {
            throw new InvalidOperationException("Auth:ClockSkewSeconds must be between 0 and 300.");
        }

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = jwt.RequireHttpsMetadata;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role"
                };
            });
    }

    private static Task WriteHealthResponseAsync(
        HttpContext context,
        HealthReport report,
        bool redactDetails)
    {
        context.Response.ContentType = "application/json";
        if (redactDetails)
        {
            return context.Response.WriteAsJsonAsync(new
            {
                status = report.Status.ToString()
            });
        }

        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    durationMs = entry.Value.Duration.TotalMilliseconds,
                    description = entry.Value.Description
                })
        });
    }
}

public sealed record PostServiceDescriptor(string Name);
