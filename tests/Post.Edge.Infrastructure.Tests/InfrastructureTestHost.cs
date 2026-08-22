using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Post.Edge.Application;
using Post.Edge.Infrastructure.Persistence;

namespace Post.Edge.Infrastructure.Tests;

internal sealed class InfrastructureTestHost : IAsyncDisposable
{
    private readonly string _databasePath;

    private InfrastructureTestHost(string databasePath, ServiceProvider services)
    {
        _databasePath = databasePath;
        Services = services;
    }

    internal ServiceProvider Services { get; }
    internal string DatabasePath => _databasePath;

    internal static async Task<InfrastructureTestHost> CreateAsync(
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? customize = null)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"post-edge-infra-{Guid.NewGuid():N}.db");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge:Storage:ConnectionString"] = $"Data Source={databasePath}",
            ["Edge:Storage:ApplyMigrationsOnStartup"] = "false",
            ["Edge:Integrations:Mode"] = "Mock",
            ["Edge:LocalAdmin:AuthenticationEnabled"] = "false",
            ["Edge:Workers:MaxOutboxAttempts"] = "2"
        };
        if (overrides is not null)
        {
            foreach (var pair in overrides) values[pair.Key] = pair.Value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddEdgeApplication();
        collection.AddEdgeInfrastructure(configuration);
        customize?.Invoke(collection);
        var provider = collection.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var host = new InfrastructureTestHost(databasePath, provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<EdgeDbContext>().Database.MigrateAsync();
        }
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        DeleteIfPresent(_databasePath);
        DeleteIfPresent(_databasePath + "-wal");
        DeleteIfPresent(_databasePath + "-shm");
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
