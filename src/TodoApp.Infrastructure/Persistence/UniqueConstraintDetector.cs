using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Decides whether a failed save was a uniqueness violation, by reading the provider's own error
/// code. This is the one place in the codebase that knows provider error numbers, and it lives in
/// Infrastructure for exactly that reason: the Application layer gets a single, engine-neutral
/// <see cref="Application.Common.Exceptions.UniqueConstraintViolationException"/> instead.
/// </summary>
/// <remarks>
/// EF Core deliberately does not classify save failures — <c>DbUpdateException</c> wraps whatever
/// the provider threw and offers no "was this a duplicate key?" question. So the alternatives are
/// to read the error code here, once, or to let every handler guess from the exception type, which
/// is what produced the bug this class exists to fix.
/// </remarks>
internal static class UniqueConstraintDetector
{
    // SQLite reports constraint failures as primary code 19 (SQLITE_CONSTRAINT), with the
    // extended code naming which constraint: 2067 = UNIQUE, 1555 = PRIMARY KEY.
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;
    private const int SqliteConstraintPrimaryKey = 1555;

    // SQL Server: 2627 = unique constraint, 2601 = duplicate key in a unique index.
    private const int SqlServerUniqueConstraint = 2627;
    private const int SqlServerDuplicateKeyIndex = 2601;

    // PostgreSQL SQLSTATE 23505 = unique_violation.
    private const string PostgresUniqueViolation = "23505";

    public static bool IsUniqueViolation(DbUpdateException exception)
    {
        // DbUpdateConcurrencyException derives from DbUpdateException but is never a constraint
        // failure — it means the UPDATE matched zero rows. The todo handlers catch it separately.
        if (exception is DbUpdateConcurrencyException)
        {
            return false;
        }

        // Walk the chain: the provider error can sit more than one level down.
        for (Exception? inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (Matches(inner))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(Exception exception) => exception switch
    {
        SqliteException sqlite =>
            sqlite.SqliteErrorCode == SqliteConstraint &&
            sqlite.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey,

        SqlException sqlServer =>
            sqlServer.Number is SqlServerUniqueConstraint or SqlServerDuplicateKeyIndex,

        PostgresException postgres =>
            postgres.SqlState == PostgresUniqueViolation,

        _ => false
    };
}
