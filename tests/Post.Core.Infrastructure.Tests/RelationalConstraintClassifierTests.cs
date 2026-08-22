using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Post.Core.Infrastructure.Persistence;

namespace Post.Core.Infrastructure.Tests;

public sealed class RelationalConstraintClassifierTests
{
    [Fact]
    public async Task ClassifiesOnlySqliteUniqueAndPrimaryKeyConstraintFailures()
    {
        var unique = await CaptureSqliteFailureAsync(
            "CREATE TABLE Items (Id INTEGER PRIMARY KEY, Code TEXT NOT NULL UNIQUE);" +
            "INSERT INTO Items (Id, Code) VALUES (1, 'A');" +
            "INSERT INTO Items (Id, Code) VALUES (2, 'A');");
        var primaryKey = await CaptureSqliteFailureAsync(
            "CREATE TABLE Items (Id INTEGER PRIMARY KEY);" +
            "INSERT INTO Items (Id) VALUES (1);" +
            "INSERT INTO Items (Id) VALUES (1);");
        var foreignKey = await CaptureSqliteFailureAsync(
            "PRAGMA foreign_keys = ON;" +
            "CREATE TABLE Parents (Id INTEGER PRIMARY KEY);" +
            "CREATE TABLE Children (Id INTEGER PRIMARY KEY, ParentId INTEGER NOT NULL " +
            "REFERENCES Parents(Id));" +
            "INSERT INTO Children (Id, ParentId) VALUES (1, 999);");

        Assert.Equal(2067, unique.SqliteExtendedErrorCode);
        Assert.Equal(1555, primaryKey.SqliteExtendedErrorCode);
        Assert.True(RelationalConstraintClassifier.IsUniqueViolation(new DbUpdateException("unique", unique)));
        Assert.True(RelationalConstraintClassifier.IsUniqueViolation(new DbUpdateException("primary", primaryKey)));
        Assert.False(RelationalConstraintClassifier.IsUniqueViolation(new DbUpdateException("foreign-key", foreignKey)));
        Assert.False(RelationalConstraintClassifier.IsUniqueViolation(
            new DbUpdateException("generic", new InvalidOperationException("not a constraint"))));
    }

    [Fact]
    public void SqlServerClassifierAcceptsOnlyDuplicateKeyNumbers()
    {
        Assert.True(RelationalConstraintClassifier.IsSqlServerUniqueNumber(2601));
        Assert.True(RelationalConstraintClassifier.IsSqlServerUniqueNumber(2627));
        Assert.False(RelationalConstraintClassifier.IsSqlServerUniqueNumber(547));
        Assert.False(RelationalConstraintClassifier.IsSqlServerUniqueNumber(1205));
        Assert.False(RelationalConstraintClassifier.IsSqlServerUniqueNumber(-2));
    }

    private static async Task<SqliteException> CaptureSqliteFailureAsync(string statements)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statements;
        return await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }
}
