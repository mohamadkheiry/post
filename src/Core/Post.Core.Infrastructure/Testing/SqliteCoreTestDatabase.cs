using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Post.Core.Infrastructure.Persistence;

namespace Post.Core.Infrastructure.Testing;

public sealed class SqliteCoreTestDatabase : IAsyncDisposable
{
    private readonly IReadOnlyCollection<SqliteConnection> _connections;
    private bool _disposed;

    private SqliteCoreTestDatabase(
        ParcelLifecycleDbContext parcelLifecycle,
        DeviceConfigDbContext deviceConfig,
        EdgeHealthDbContext edgeHealth,
        OperationalResultDbContext operationalResult,
        IdentityDbContext identity,
        IReadOnlyCollection<SqliteConnection> connections)
    {
        ParcelLifecycle = parcelLifecycle;
        DeviceConfig = deviceConfig;
        EdgeHealth = edgeHealth;
        OperationalResult = operationalResult;
        Identity = identity;
        _connections = connections;
    }

    public ParcelLifecycleDbContext ParcelLifecycle { get; }

    public DeviceConfigDbContext DeviceConfig { get; }

    public EdgeHealthDbContext EdgeHealth { get; }

    public OperationalResultDbContext OperationalResult { get; }

    public IdentityDbContext Identity { get; }

    public static async Task<SqliteCoreTestDatabase> CreateAsync(CancellationToken cancellationToken = default)
    {
        var connections = Enumerable.Range(0, 5)
            .Select(_ => new SqliteConnection("Data Source=:memory:"))
            .ToArray();
        try
        {
            foreach (var connection in connections)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            var parcel = new ParcelLifecycleDbContext(
                new DbContextOptionsBuilder<ParcelLifecycleDbContext>().UseSqlite(connections[0]).Options);
            var device = new DeviceConfigDbContext(
                new DbContextOptionsBuilder<DeviceConfigDbContext>().UseSqlite(connections[1]).Options);
            var health = new EdgeHealthDbContext(
                new DbContextOptionsBuilder<EdgeHealthDbContext>().UseSqlite(connections[2]).Options);
            var result = new OperationalResultDbContext(
                new DbContextOptionsBuilder<OperationalResultDbContext>().UseSqlite(connections[3]).Options);
            var identity = new IdentityDbContext(
                new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(connections[4]).Options);
            var database = new SqliteCoreTestDatabase(parcel, device, health, result, identity, connections);
            await database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            return database;
        }
        catch
        {
            foreach (var connection in connections)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await ParcelLifecycle.DisposeAsync().ConfigureAwait(false);
        await DeviceConfig.DisposeAsync().ConfigureAwait(false);
        await EdgeHealth.DisposeAsync().ConfigureAwait(false);
        await OperationalResult.DisposeAsync().ConfigureAwait(false);
        await Identity.DisposeAsync().ConfigureAwait(false);
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        await ParcelLifecycle.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await DeviceConfig.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await EdgeHealth.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await OperationalResult.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        await Identity.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }
}
