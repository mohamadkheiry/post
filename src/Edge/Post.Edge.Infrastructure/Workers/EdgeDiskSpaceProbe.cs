using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Workers;

public sealed class EdgeDiskSpaceProbe(IOptions<EdgeStorageOptions> options) : IDiskSpaceProbe
{
    public long GetAvailableBytes()
    {
        var builder = new SqliteConnectionStringBuilder(options.Value.ConnectionString);
        var dataSource = builder.DataSource;
        var location = string.IsNullOrWhiteSpace(dataSource) ||
                       string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase)
            ? Environment.CurrentDirectory
            : Path.GetFullPath(dataSource);
        var root = Path.GetPathRoot(location);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Edge storage drive could not be resolved.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
