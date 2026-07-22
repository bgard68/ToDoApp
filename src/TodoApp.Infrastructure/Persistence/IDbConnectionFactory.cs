using System.Data.Common;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Creates provider-specific ADO.NET connections for Dapper. Replaces EF's
/// <c>AddDbContext</c> provider wiring; the concrete factory reads <c>Database:Provider</c>
/// and the connection string once at construction.
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>The configured backend, used by repositories to select dialect-specific SQL.</summary>
    DbProvider Provider { get; }

    /// <summary>Creates a new, unopened connection.</summary>
    DbConnection Create();

    /// <summary>
    /// Creates and opens a connection. For SQL Server, transient failures — notably the
    /// Azure SQL serverless "waking from auto-pause" timeout (error -2) — are retried, so the
    /// first request after the database has been idle succeeds instead of throwing.
    /// </summary>
    Task<DbConnection> CreateOpenAsync(CancellationToken cancellationToken);
}
