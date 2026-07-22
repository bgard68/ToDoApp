using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace TodoApp.Infrastructure.Persistence;

internal static class DbExceptionExtensions
{
    /// <summary>
    /// True when the exception represents a unique-constraint / duplicate-key violation on either
    /// provider. SQLite raises SQLITE_CONSTRAINT (19); SQL Server raises 2601 (duplicate index key)
    /// or 2627 (unique constraint).
    /// </summary>
    public static bool IsUniqueConstraintViolation(this DbException exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19,
        SqlException sql => sql.Errors.Cast<SqlError>().Any(e => e.Number is 2601 or 2627),
        _ => false
    };
}
