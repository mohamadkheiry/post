using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Post.Edge.Infrastructure.Persistence;

public sealed class EdgeDesignTimeDbContextFactory : IDesignTimeDbContextFactory<EdgeDbContext>
{
    public EdgeDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_SQLITE_CONNECTION")
                               ?? "Data Source=edge.design.db";
        var options = new DbContextOptionsBuilder<EdgeDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new EdgeDbContext(options);
    }
}
