using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Post.Core.Application.Common;

namespace Post.Core.Infrastructure.Persistence;

internal static class RelationalConstraintClassifier
{
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintPrimaryKey = 1555;
    private const int SqliteConstraintUnique = 2067;

    public static async Task SaveChangesAsync(
        DbContext dbContext,
        string storeName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new UniqueConstraintViolationException(
                string.Concat(storeName, " database unique constraint was violated."),
                exception);
        }
    }

    internal static bool IsUniqueViolation(DbUpdateException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException && IsSqlServerUniqueNumber(sqlException.Number))
            {
                return true;
            }

            if (current is SqliteException sqliteException &&
                sqliteException.SqliteErrorCode == SqliteConstraint &&
                sqliteException.SqliteExtendedErrorCode is SqliteConstraintPrimaryKey or SqliteConstraintUnique)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsSqlServerUniqueNumber(int number) => number is 2601 or 2627;
}
