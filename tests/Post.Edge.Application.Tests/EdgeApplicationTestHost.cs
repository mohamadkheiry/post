using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Post.Edge.Application.Ports;
using Post.Edge.Domain.Bags;
using Post.Edge.Domain.Parcels;
using Post.Edge.Domain.ValueObjects;
using Post.Edge.Infrastructure;
using Post.Edge.Infrastructure.Persistence;

namespace Post.Edge.Application.Tests;

internal sealed class EdgeApplicationTestHost : IAsyncDisposable
{
    private readonly string _databasePath;

    private EdgeApplicationTestHost(string databasePath, ServiceProvider services)
    {
        _databasePath = databasePath;
        Services = services;
    }

    internal ServiceProvider Services { get; }
    internal string DatabasePath => _databasePath;

    internal static async Task<EdgeApplicationTestHost> CreateAsync(
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? customize = null)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"post-edge-app-{Guid.NewGuid():N}.db");
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Edge:Storage:ConnectionString"] = $"Data Source={databasePath}",
            ["Edge:Storage:ApplyMigrationsOnStartup"] = "false",
            ["Edge:Integrations:Mode"] = "Mock",
            ["Edge:Security:AllowUnboundDevices"] = "true",
            ["Edge:Mock:CenterCode"] = "59544",
            ["Edge:Mock:ConfigurationVersion"] = "1",
            ["Edge:Mock:Devices:0:DeviceId"] = "device-1",
            ["Edge:Mock:Devices:0:LogicalId"] = "SORT-1",
            ["Edge:Mock:Devices:0:Token"] = "test-token",
            ["Edge:Mock:Devices:0:IsActive"] = "true"
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
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var host = new EdgeApplicationTestHost(databasePath, provider);
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<EdgeDbContext>().Database.MigrateAsync();
        }
        return host;
    }

    internal async Task<TResult> InvokeAsync<TService, TResult>(Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    internal async Task InvokeAsync<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    internal async Task<EdgeParcel> SeedAssignedParcelAsync(
        string barcode,
        DateTimeOffset timestamp,
        bool deferred = false)
    {
        await InvokeAsync<Post.Edge.Application.Services.IConfigurationSyncService>(
            service => service.SyncAsync());
        await using var scope = Services.CreateAsyncScope();
        var parcel = EdgeParcel.Create(
            Guid.NewGuid(), BarcodeSet.Create([barcode]), "device-1", timestamp,
            new PhysicalAttributes(100, null), serviceType: "Express");
        parcel.AssignDestination(PostalCenterCode.Create("123"), "C1");
        if (deferred) parcel.MarkDeferred(new ParcelError("OLD", "Earlier failure"));
        scope.ServiceProvider.GetRequiredService<IEdgeParcelRepository>().Add(parcel);
        await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(default);
        return parcel;
    }

    internal async Task<EdgeBag> SeedBagAsync(string bagBarcode)
    {
        await using var scope = Services.CreateAsyncScope();
        var bag = EdgeBag.Create(
            bagBarcode,
            Guid.NewGuid(),
            "device-1",
            PostalCenterCode.Create("123"),
            ["PARCEL-1"],
            [1],
            "SEAL-1",
            "Road",
            new BagCounts(1, 0, 0, 0),
            null,
            DateTimeOffset.UtcNow);
        scope.ServiceProvider.GetRequiredService<IEdgeBagRepository>().Add(bag);
        await scope.ServiceProvider.GetRequiredService<IEdgeUnitOfWork>().SaveChangesAsync(default);
        return bag;
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
