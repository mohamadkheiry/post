using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Post.Core.Infrastructure.Persistence;

public sealed class ParcelLifecycleDesignTimeDbContextFactory :
    IDesignTimeDbContextFactory<ParcelLifecycleDbContext>
{
    public ParcelLifecycleDbContext CreateDbContext(string[] args) =>
        new(CreateSqlServerOptions<ParcelLifecycleDbContext>("PostParcelLifecycle"));

    private static DbContextOptions<TContext> CreateSqlServerOptions<TContext>(string databaseName)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(CreateConnectionString(databaseName))
            .Options;

    private static string CreateConnectionString(string databaseName) =>
        $"Server=(localdb)\\MSSQLLocalDB;Database={databaseName};Trusted_Connection=True;TrustServerCertificate=True";
}

public sealed class DeviceConfigDesignTimeDbContextFactory : IDesignTimeDbContextFactory<DeviceConfigDbContext>
{
    public DeviceConfigDbContext CreateDbContext(string[] args) =>
        new(CreateSqlServerOptions<DeviceConfigDbContext>());

    private static DbContextOptions<TContext> CreateSqlServerOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=PostDeviceConfig;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
}

public sealed class EdgeHealthDesignTimeDbContextFactory : IDesignTimeDbContextFactory<EdgeHealthDbContext>
{
    public EdgeHealthDbContext CreateDbContext(string[] args) =>
        new(CreateSqlServerOptions<EdgeHealthDbContext>());

    private static DbContextOptions<TContext> CreateSqlServerOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=PostEdgeHealth;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
}

public sealed class OperationalResultDesignTimeDbContextFactory :
    IDesignTimeDbContextFactory<OperationalResultDbContext>
{
    public OperationalResultDbContext CreateDbContext(string[] args) =>
        new(CreateSqlServerOptions<OperationalResultDbContext>());

    private static DbContextOptions<TContext> CreateSqlServerOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=PostOperationalResult;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
}

public sealed class IdentityDesignTimeDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        new(CreateSqlServerOptions<IdentityDbContext>());

    private static DbContextOptions<TContext> CreateSqlServerOptions<TContext>()
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseSqlServer(
                "Server=(localdb)\\MSSQLLocalDB;Database=PostIdentity;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
}
