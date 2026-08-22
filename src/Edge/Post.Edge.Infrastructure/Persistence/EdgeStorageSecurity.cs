using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;

namespace Post.Edge.Infrastructure.Persistence;

public interface IEdgeStorageMaintenance
{
    Task PrepareAsync(CancellationToken cancellationToken = default);

    Task<string> CreateEncryptedBackupAsync(CancellationToken cancellationToken = default);
}

internal sealed record EdgeStorageKeyMaterial(string Current, string? Previous);

internal interface IEdgeStorageKeyProvider
{
    EdgeStorageKeyMaterial GetRequiredKeys();
}

internal sealed class EnvironmentEdgeStorageKeyProvider(IOptions<EdgeStorageOptions> options)
    : IEdgeStorageKeyProvider
{
    private const int MinimumKeyLength = 32;
    private const int MaximumKeyLength = 256;

    public EdgeStorageKeyMaterial GetRequiredKeys()
    {
        var settings = options.Value;
        var current = ReadRequired(settings.EncryptionKeyEnvironmentVariable);
        var previous = ReadOptional(settings.PreviousEncryptionKeyEnvironmentVariable);
        return new EdgeStorageKeyMaterial(current, previous);
    }

    private static string ReadRequired(string variableName) =>
        ReadOptional(variableName) ?? throw new InvalidOperationException(
            "The external Edge storage encryption key is unavailable.");

    private static string? ReadOptional(string variableName)
    {
        ValidateVariableName(variableName);
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length is < MinimumKeyLength or > MaximumKeyLength || value.Any(char.IsControl))
            throw new InvalidOperationException("The external Edge storage encryption key is invalid.");
        return value;
    }

    private static void ValidateVariableName(string variableName)
    {
        if (string.IsNullOrWhiteSpace(variableName) ||
            variableName.Length > 128 ||
            variableName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new InvalidOperationException("The Edge storage key source name is invalid.");
        }
    }
}

internal static class EdgeSqliteRuntime
{
    private static readonly object Sync = new();
    private static bool _initialized;

    internal static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Sync)
        {
            if (_initialized) return;
            SQLitePCL.Batteries_V2.Init();
            _initialized = true;
        }
    }
}

internal static class EdgeStorageConnection
{
    internal static string Build(
        EdgeStorageOptions options,
        EdgeStorageKeyMaterial? keys,
        bool disablePooling = false,
        string? dataSource = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
        if (!string.IsNullOrEmpty(builder.Password))
            throw new InvalidOperationException(
                "Edge storage passwords must be supplied by the configured external key source.");
        if (dataSource is not null) builder.DataSource = dataSource;
        if (options.EncryptionEnabled)
        {
            if (keys is null)
                throw new InvalidOperationException("Edge storage encryption key material is unavailable.");
            builder.Password = keys.Current;
        }
        if (disablePooling) builder.Pooling = false;
        return builder.ToString();
    }

    internal static string ResolveDatabasePath(EdgeStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var builder = new SqliteConnectionStringBuilder(options.ConnectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) ||
            string.Equals(builder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
            builder.Mode == SqliteOpenMode.Memory)
        {
            throw new InvalidOperationException("Encrypted Edge storage requires a file-backed database.");
        }
        return Path.GetFullPath(builder.DataSource);
    }
}

internal sealed class EdgeStorageMaintenance(
    IOptions<EdgeStorageOptions> options,
    IEdgeStorageKeyProvider keyProvider,
    IClock clock) : IEdgeStorageMaintenance
{
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        EdgeSqliteRuntime.EnsureInitialized();
        var settings = options.Value;
        if (!settings.EncryptionEnabled) return;

        var keys = keyProvider.GetRequiredKeys();
        var databasePath = EdgeStorageConnection.ResolveDatabasePath(settings);
        if (!File.Exists(databasePath) && !string.IsNullOrWhiteSpace(settings.RestoreFromBackupPath))
            await RestoreAsync(settings.RestoreFromBackupPath, databasePath, keys, cancellationToken);

        if (!File.Exists(databasePath))
        {
            await ValidateOrCreateAsync(databasePath, keys.Current, cancellationToken);
            return;
        }

        if (await CanReadAsync(databasePath, keys.Current, cancellationToken)) return;
        if (keys.Previous is null || !await CanReadAsync(databasePath, keys.Previous, cancellationToken))
            throw new InvalidOperationException("Encrypted Edge storage could not be opened with an active key.");

        await RekeyAsync(databasePath, keys.Previous, keys.Current, cancellationToken);
        SqliteConnection.ClearAllPools();
        if (!await CanReadAsync(databasePath, keys.Current, cancellationToken))
            throw new InvalidOperationException("Edge storage key rotation could not be verified.");
    }

    public async Task<string> CreateEncryptedBackupAsync(CancellationToken cancellationToken = default)
    {
        EdgeSqliteRuntime.EnsureInitialized();
        var settings = options.Value;
        if (!settings.EncryptionEnabled)
            throw new InvalidOperationException("Encrypted Edge backups require storage encryption.");
        var keys = keyProvider.GetRequiredKeys();
        var sourcePath = EdgeStorageConnection.ResolveDatabasePath(settings);
        var directory = Path.GetFullPath(settings.BackupDirectory);
        Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(
            directory,
            $"edge-{clock.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.db");

        await using var source = CreateConnection(sourcePath, keys.Current);
        await source.OpenAsync(cancellationToken);
        await VerifyReadableAsync(source, cancellationToken);
        await using var destination = CreateConnection(backupPath, keys.Current);
        await destination.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
        await VerifyReadableAsync(destination, cancellationToken);
        return backupPath;
    }

    private async Task RestoreAsync(
        string backupPath,
        string databasePath,
        EdgeStorageKeyMaterial keys,
        CancellationToken cancellationToken)
    {
        var resolvedBackup = Path.GetFullPath(backupPath);
        if (!File.Exists(resolvedBackup) ||
            string.Equals(resolvedBackup, databasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The configured Edge storage backup is unavailable.");
        }

        var backupKey = await CanReadAsync(resolvedBackup, keys.Current, cancellationToken)
            ? keys.Current
            : keys.Previous is not null && await CanReadAsync(resolvedBackup, keys.Previous, cancellationToken)
                ? keys.Previous
                : null;
        if (backupKey is null)
            throw new InvalidOperationException("The configured Edge storage backup could not be verified.");

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var source = CreateConnection(resolvedBackup, backupKey);
        await source.OpenAsync(cancellationToken);
        await VerifyReadableAsync(source, cancellationToken);
        await using var destination = CreateConnection(databasePath, keys.Current);
        await destination.OpenAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
        await VerifyReadableAsync(destination, cancellationToken);
    }

    private async Task ValidateOrCreateAsync(
        string databasePath,
        string key,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = CreateConnection(databasePath, key);
        await connection.OpenAsync(cancellationToken);
        await VerifyReadableAsync(connection, cancellationToken);
    }

    private async Task<bool> CanReadAsync(
        string databasePath,
        string key,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = CreateConnection(databasePath, key);
            await connection.OpenAsync(cancellationToken);
            await VerifyReadableAsync(connection, cancellationToken);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private async Task RekeyAsync(
        string databasePath,
        string previousKey,
        string currentKey,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(databasePath, previousKey);
        await connection.OpenAsync(cancellationToken);
        await VerifyReadableAsync(connection, cancellationToken);
        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT quote($newKey);";
        command.Parameters.AddWithValue("$newKey", currentKey);
        var quoted = (string?)await command.ExecuteScalarAsync(cancellationToken)
                     ?? throw new InvalidOperationException("Edge storage key rotation could not be prepared.");
        command.Parameters.Clear();
        command.CommandText = "PRAGMA rekey = " + quoted;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await VerifyReadableAsync(connection, cancellationToken);
    }

    private SqliteConnection CreateConnection(string path, string key)
    {
        var settings = options.Value;
        var keys = new EdgeStorageKeyMaterial(key, null);
        return new SqliteConnection(EdgeStorageConnection.Build(settings, keys, true, path));
    }

    private static async Task VerifyReadableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master;";
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
