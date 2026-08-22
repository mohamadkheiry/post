using System.Text;
using Microsoft.Data.Sqlite;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Persistence;

namespace Post.Edge.Infrastructure.Tests;

public sealed class EdgeStorageSecurityTests
{
    [Fact]
    public async Task EncryptedStorageProtectsDatabaseWalAndSharedMemoryFiles()
    {
        using var fixture = new StorageFixture();
        var key = new string('K', 48);
        fixture.SetCurrentKey(key);
        var maintenance = fixture.CreateMaintenance();
        await maintenance.PrepareAsync(TestContext.Current.CancellationToken);

        const string sentinel = "confidential-parcel-evidence-59544";
        await using var connection = CreateConnection(fixture.DatabasePath, key);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;");
        await ExecuteAsync(connection, "CREATE TABLE Evidence(Value TEXT NOT NULL);");
        await ExecuteAsync(connection, $"INSERT INTO Evidence VALUES ('{sentinel}');");

        Assert.True(File.Exists(fixture.DatabasePath));
        Assert.True(File.Exists(fixture.DatabasePath + "-wal"));
        Assert.True(File.Exists(fixture.DatabasePath + "-shm"));
        Assert.DoesNotContain(
            "SQLite format 3",
            Encoding.ASCII.GetString(await ReadAllBytesSharedAsync(fixture.DatabasePath)),
            StringComparison.Ordinal);
        foreach (var path in new[]
                 {
                     fixture.DatabasePath,
                     fixture.DatabasePath + "-wal",
                     fixture.DatabasePath + "-shm"
                 })
        {
            Assert.DoesNotContain(
                sentinel,
                Encoding.UTF8.GetString(await ReadAllBytesSharedAsync(path)),
                StringComparison.Ordinal);
        }

        await AssertCannotReadAsync(fixture.DatabasePath, new string('X', 48));
    }

    [Fact]
    public async Task StagedKeyRotationPreservesDataAndRejectsOldKey()
    {
        using var fixture = new StorageFixture();
        var oldKey = new string('O', 48);
        var newKey = new string('N', 48);
        fixture.SetCurrentKey(oldKey);
        await fixture.CreateMaintenance().PrepareAsync(TestContext.Current.CancellationToken);
        await using (var connection = CreateConnection(fixture.DatabasePath, oldKey))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(connection, "CREATE TABLE RotationProof(Value TEXT NOT NULL);");
            await ExecuteAsync(connection, "INSERT INTO RotationProof VALUES ('preserved');");
        }
        SqliteConnection.ClearAllPools();

        fixture.SetCurrentKey(newKey);
        fixture.SetPreviousKey(oldKey);
        await fixture.CreateMaintenance().PrepareAsync(TestContext.Current.CancellationToken);

        await AssertCannotReadAsync(fixture.DatabasePath, oldKey);
        await using var verified = CreateConnection(fixture.DatabasePath, newKey);
        await verified.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = verified.CreateCommand();
        command.CommandText = "SELECT Value FROM RotationProof;";
        Assert.Equal("preserved", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        await verified.CloseAsync();

        fixture.SetPreviousKey(null);
        await fixture.CreateMaintenance().PrepareAsync(TestContext.Current.CancellationToken);
        fixture.SetCurrentKey(new string('W', 48));
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.CreateMaintenance().PrepareAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(oldKey, rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(newKey, rejected.Message, StringComparison.Ordinal);
        fixture.SetCurrentKey(newKey);
    }

    [Fact]
    public async Task EncryptedBackupRestoresAfterDatabaseLoss()
    {
        using var fixture = new StorageFixture();
        var key = new string('B', 48);
        fixture.SetCurrentKey(key);
        var maintenance = fixture.CreateMaintenance();
        await maintenance.PrepareAsync(TestContext.Current.CancellationToken);
        await using (var connection = CreateConnection(fixture.DatabasePath, key))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await ExecuteAsync(connection, "CREATE TABLE BackupProof(Value TEXT NOT NULL);");
            await ExecuteAsync(connection, "INSERT INTO BackupProof VALUES ('restored');");
        }

        var backupPath = await maintenance.CreateEncryptedBackupAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(backupPath));
        Assert.DoesNotContain(
            "restored",
            Encoding.UTF8.GetString(await ReadAllBytesSharedAsync(backupPath)),
            StringComparison.Ordinal);
        await AssertCannotReadAsync(backupPath, new string('W', 48));

        SqliteConnection.ClearAllPools();
        File.Delete(fixture.DatabasePath);
        fixture.Options.RestoreFromBackupPath = backupPath;
        await fixture.CreateMaintenance().PrepareAsync(TestContext.Current.CancellationToken);

        await using var restored = CreateConnection(fixture.DatabasePath, key);
        await restored.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = restored.CreateCommand();
        command.CommandText = "SELECT Value FROM BackupProof;";
        Assert.Equal("restored", await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExternalKeyProviderRejectsMissingWeakAndMalformedMaterial()
    {
        using var fixture = new StorageFixture();
        var provider = new EnvironmentEdgeStorageKeyProvider(
            Microsoft.Extensions.Options.Options.Create(fixture.Options));

        var missing = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeys());
        Assert.DoesNotContain(fixture.CurrentVariable, missing.Message, StringComparison.Ordinal);

        fixture.SetCurrentKey("short");
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeys());
        fixture.SetCurrentKey(new string('A', 31) + "\n");
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredKeys());
    }

    private static SqliteConnection CreateConnection(string path, string key) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Password = key,
            Pooling = false
        }.ToString());

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertCannotReadAsync(string path, string key)
    {
        await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await using var connection = CreateConnection(path, key);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master;";
            await command.ExecuteScalarAsync();
        });
    }

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4_096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }

    private sealed class StorageFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            $"post-edge-encrypted-{Guid.NewGuid():N}");

        internal StorageFixture()
        {
            Directory.CreateDirectory(_root);
            DatabasePath = Path.Combine(_root, "edge.db");
            CurrentVariable = $"POST_EDGE_TEST_KEY_{Guid.NewGuid():N}";
            PreviousVariable = $"POST_EDGE_TEST_PREVIOUS_KEY_{Guid.NewGuid():N}";
            Options = new EdgeStorageOptions
            {
                ConnectionString = $"Data Source={DatabasePath}",
                EncryptionEnabled = true,
                EncryptionKeyEnvironmentVariable = CurrentVariable,
                PreviousEncryptionKeyEnvironmentVariable = PreviousVariable,
                BackupDirectory = Path.Combine(_root, "backups")
            };
        }

        internal EdgeStorageOptions Options { get; }
        internal string DatabasePath { get; }
        internal string CurrentVariable { get; }
        internal string PreviousVariable { get; }

        internal void SetCurrentKey(string? key) => Environment.SetEnvironmentVariable(CurrentVariable, key);

        internal void SetPreviousKey(string? key) => Environment.SetEnvironmentVariable(PreviousVariable, key);

        internal EdgeStorageMaintenance CreateMaintenance() => new(
            Microsoft.Extensions.Options.Options.Create(Options),
            new EnvironmentEdgeStorageKeyProvider(Microsoft.Extensions.Options.Options.Create(Options)),
            new FixedClock());

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(CurrentVariable, null);
            Environment.SetEnvironmentVariable(PreviousVariable, null);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }
}
