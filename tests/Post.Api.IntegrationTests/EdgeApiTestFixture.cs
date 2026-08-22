using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Post.BuildingBlocks.Serialization;

namespace Post.Api.IntegrationTests;

internal sealed class EdgeApiFactory : WebApplicationFactory<Post.Edge.Api.EdgeApiMarker>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly bool _deleteDatabaseOnDispose;

    internal EdgeApiFactory(
        IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null,
        string? databasePath = null,
        bool deleteDatabaseOnDispose = true)
    {
        DatabasePath = databasePath ?? Path.Combine(Path.GetTempPath(), $"post-edge-api-{Guid.NewGuid():N}.db");
        _settings = settings ?? new Dictionary<string, string?>();
        _configureServices = configureServices;
        _deleteDatabaseOnDispose = deleteDatabaseOnDispose;
    }

    internal string DatabasePath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Edge:Storage:ConnectionString", $"Data Source={DatabasePath}");
        foreach (var setting in _settings.Where(setting => setting.Value is not null))
            builder.UseSetting(setting.Key, setting.Value);
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Api:AuthenticationEnabled"] = "false",
                ["Api:SwaggerEnabled"] = "false",
                ["Api:MetricsEnabled"] = "false",
                ["Edge:Integrations:Profile"] = "Mock",
                ["Edge:Storage:ConnectionString"] = $"Data Source={DatabasePath}",
                ["Edge:Storage:ApplyMigrationsOnStartup"] = "true",
                ["Edge:Security:AllowUnboundDevices"] = "true",
                ["Edge:Mock:CenterCode"] = "59544",
                ["Edge:Mock:ConfigurationVersion"] = "1",
                ["Edge:Mock:Devices:0:DeviceId"] = "device-1",
                ["Edge:Mock:Devices:0:LogicalId"] = "SORT-1",
                ["Edge:Mock:Devices:0:Token"] = "testing-device-token",
                ["Edge:Mock:Devices:0:IsActive"] = "true",
                ["Edge:DeviceTransport:AuthenticationDeadline"] = "00:00:02",
                ["Edge:DeviceTransport:FailedAuthenticationCloseDelay"] = "00:00:00.100",
                ["Edge:DeviceTransport:ConnectionPermitLimit"] = "100",
                ["Edge:Workers:PollInterval"] = "01:00:00"
            };
            foreach (var setting in _settings) values[setting.Key] = setting.Value;
            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, EdgeApiTestRemoteAddressStartupFilter>();
            _configureServices?.Invoke(services);
        });
    }

    internal HubConnection CreateHubConnection(IReadOnlyDictionary<string, string>? headers = null)
    {
        using var startupClient = CreateClient();
        return new HubConnectionBuilder()
            .WithUrl("http://localhost/ws/device", options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                if (headers is not null)
                {
                    foreach (var header in headers) options.Headers[header.Key] = header.Value;
                }
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var webSocketClient = Server.CreateWebSocketClient();
                    webSocketClient.ConfigureRequest = request =>
                    {
                        if (headers is not null)
                        {
                            foreach (var header in headers)
                                request.Headers[header.Key] = header.Value;
                        }
                    };
                    return await webSocketClient.ConnectAsync(context.Uri, cancellationToken);
                };
            })
            .AddJsonProtocol(options => PostJsonDefaults.Apply(options.PayloadSerializerOptions))
            .Build();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !_deleteDatabaseOnDispose) return;
        SqliteConnection.ClearAllPools();
        DeleteIfPresent(DatabasePath);
        DeleteIfPresent(DatabasePath + "-wal");
        DeleteIfPresent(DatabasePath + "-shm");
    }

    private static void DeleteIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A host that fails during startup can leave an EF finalizer holding the temporary
            // file until process teardown. Cleanup must not mask the startup assertion.
        }
    }
}

internal sealed class EdgeApiTestRemoteAddressStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return app =>
        {
            app.Use((context, following) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return following(context);
            });
            next(app);
        };
    }
}
