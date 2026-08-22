using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Post.Core.Application.Contracts;
using Post.Core.Infrastructure.Security;

namespace Post.Api.IntegrationTests;

internal sealed class CoreApiFactory<TMarker> : WebApplicationFactory<TMarker>
    where TMarker : class
{
    private readonly string _databaseDirectory = Path.Combine(
        Path.GetTempPath(),
        "post-core-host-tests",
        Guid.NewGuid().ToString("N"));
    private readonly IReadOnlyDictionary<string, string?> _additionalSettings;
    private readonly ConcurrentQueue<string> _logMessages = new();

    public CoreApiFactory(IReadOnlyDictionary<string, string?>? additionalSettings = null) =>
        _additionalSettings = additionalSettings ?? new Dictionary<string, string?>();

    public IReadOnlyCollection<string> LogMessages => _logMessages.ToArray();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Host settings are visible while top-level Program registers DbContexts;
        // ConfigureAppConfiguration alone runs too late for that registration decision.
        builder.UseSetting("Core:Database:Directory", _databaseDirectory);
        foreach (var pair in _additionalSettings.Where(x => x.Value is not null))
        {
            builder.UseSetting(pair.Key, pair.Value);
        }

        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Api:AuthenticationEnabled"] = "true",
                ["Api:SwaggerEnabled"] = "false",
                ["Api:MetricsEnabled"] = "false",
                ["Auth:Issuer"] = CoreApiTestClient.Issuer,
                ["Auth:Audience"] = CoreApiTestClient.Audience,
                ["Auth:SigningKey"] = CoreApiTestClient.SigningKey,
                ["Auth:RequireHttpsMetadata"] = "false",
                ["Core:Database:Provider"] = "Sqlite",
                ["Core:Database:Directory"] = _databaseDirectory,
                ["Core:Database:ApplyMigrations"] = "false",
                ["Core:Outbox:Enabled"] = "false"
            };
            foreach (var pair in _additionalSettings)
            {
                settings[pair.Key] = pair.Value;
            }

            configuration.AddInMemoryCollection(settings);
        });
        builder.ConfigureServices(services =>
            services.AddSingleton<ILoggerProvider>(_ => new CoreApiLogProvider(_logMessages)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_databaseDirectory))
        {
            Directory.Delete(_databaseDirectory, recursive: true);
        }
    }
}

internal sealed class CoreApiLogProvider(ConcurrentQueue<string> messages) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CoreApiLogger(categoryName, messages);

    public void Dispose()
    {
    }

    private sealed class CoreApiLogger(string categoryName, ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                messages.Enqueue(string.Concat(
                    categoryName,
                    " scope: ",
                    string.Join(", ", values.Select(value => string.Concat(value.Key, "=", value.Value)))));
            }

            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            messages.Enqueue(
                $"{categoryName}: {formatter(state, exception)}{(exception is null ? string.Empty : $" | {exception}")}");
        }
    }
}

internal static class CoreApiTestClient
{
    public const string Issuer = "post-core-tests";
    public const string Audience = "post-platform-tests";
    public const string SigningKey = "testing-only-post-platform-signing-key-32-bytes";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string EdgeToken(Guid deviceId, params string[] centers) => IssueToken(
        deviceId.ToString("D"),
        ["Edge"],
        centers,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device_id"] = deviceId.ToString("D")
        });

    public static string EdgeTokenWithRoles(
        Guid deviceId,
        IReadOnlyCollection<string> centers,
        params string[] additionalRoles) => IssueToken(
        deviceId.ToString("D"),
        ["Edge", .. additionalRoles],
        centers,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["device_id"] = deviceId.ToString("D")
        });

    public static string AdministratorToken(
        string role = "SystemAdministrator",
        params string[] centers) => IssueToken(
        Guid.NewGuid().ToString("D"),
        [role],
        centers,
        new Dictionary<string, string>(StringComparer.Ordinal));

    public static HttpRequestMessage JsonRequest(
        HttpMethod method,
        string path,
        object? payload = null,
        string? token = null,
        string? idempotencyKey = null,
        string correlationId = "core-api-test-correlation")
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: JsonOptions);
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        request.Headers.Add("X-Correlation-ID", correlationId);
        return request;
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        return value ?? throw new InvalidOperationException("The API response did not contain the expected JSON payload.");
    }

    private static string IssueToken(
        string subject,
        IReadOnlyCollection<string> roles,
        IReadOnlyCollection<string> centers,
        IReadOnlyDictionary<string, string> additionalClaims)
    {
        var now = DateTimeOffset.UtcNow;
        var issuer = new HmacJwtTokenIssuer(new HmacJwtOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            SigningKey = SigningKey
        });
        return issuer.Issue(new TokenDescriptor(
            subject,
            roles,
            centers,
            additionalClaims,
            now,
            now.AddHours(1))).Token;
    }
}
