using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Provider-aware connection factory. Chosen backend and connection string are resolved once
/// from configuration so the same build runs on SQLite locally and Azure SQL in production
/// (set <c>Database:Provider=SqlServer</c> + a connection string).
/// </summary>
public sealed class DbConnectionFactory : IDbConnectionFactory
{
    // Transient SQL Server / Azure SQL error numbers worth retrying on connect. -2 is the
    // client-side timeout raised while a serverless database wakes from auto-pause; the rest
    // are the common Azure transient/throttling codes. Mirrors the intent of the EF Core
    // EnableRetryOnFailure policy this factory replaces.
    private static readonly HashSet<int> TransientErrorNumbers =
        new() { -2, 20, 64, 233, 4060, 4221, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613, 49918, 49919, 49920 };

    private const int MaxRetries = 8;
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);

    private readonly DbProvider _provider;
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        var provider = configuration.GetValue<string>("Database:Provider") ?? "Sqlite";
        if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            _provider = DbProvider.SqlServer;
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "ConnectionStrings:DefaultConnection is required when Database:Provider is SqlServer.");
        }
        else
        {
            _provider = DbProvider.Sqlite;
            var raw = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=todoapp.db";
            // Enable FK enforcement (EF Core turned this on) so ON DELETE SET NULL / CASCADE fire.
            _connectionString = new SqliteConnectionStringBuilder(raw) { ForeignKeys = true }.ConnectionString;
        }
    }

    public DbProvider Provider => _provider;

    public DbConnection Create() => _provider switch
    {
        DbProvider.SqlServer => new SqlConnection(_connectionString),
        _ => new SqliteConnection(_connectionString)
    };

    public async Task<DbConnection> CreateOpenAsync(CancellationToken cancellationToken)
    {
        if (_provider != DbProvider.SqlServer)
        {
            var sqlite = new SqliteConnection(_connectionString);
            await sqlite.OpenAsync(cancellationToken);
            return sqlite;
        }

        // SQL Server: retry transient open failures with capped exponential backoff.
        for (var attempt = 1; ; attempt++)
        {
            var connection = new SqlConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch (SqlException ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                await connection.DisposeAsync();
                var delayMs = Math.Min(MaxDelay.TotalMilliseconds, Math.Pow(2, attempt) * 100);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }

    private static bool IsTransient(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (TransientErrorNumbers.Contains(error.Number))
            {
                return true;
            }
        }

        return false;
    }
}
