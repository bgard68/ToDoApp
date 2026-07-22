using System.Data.Common;
using Dapper;

namespace TodoApp.Infrastructure.Persistence.Repositories;

/// <summary>
/// Shared plumbing for Dapper repositories: resolves the scope's connection and ambient
/// transaction, and performs identity inserts with provider-specific new-id retrieval.
/// </summary>
public abstract class RepositoryBase
{
    private readonly IDbConnectionContext _context;
    private readonly IDbConnectionFactory _factory;

    protected RepositoryBase(IDbConnectionContext context, IDbConnectionFactory factory)
    {
        _context = context;
        _factory = factory;
    }

    protected ValueTask<DbConnection> ConnectionAsync(CancellationToken cancellationToken)
        => _context.GetConnectionAsync(cancellationToken);

    protected DbTransaction? Transaction => _context.Transaction;

    /// <summary>Builds a Dapper command bound to the ambient transaction.</summary>
    protected CommandDefinition Command(string sql, object? param, CancellationToken cancellationToken)
        => new(sql, param, Transaction, cancellationToken: cancellationToken);

    /// <summary>Runs an INSERT and returns the generated identity value.</summary>
    protected async Task<int> InsertAsync(string insertSql, object param, CancellationToken cancellationToken)
    {
        var identitySql = _factory.Provider == DbProvider.SqlServer
            ? "SELECT CAST(SCOPE_IDENTITY() AS INT);"
            : "SELECT last_insert_rowid();";

        var connection = await ConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            Command($"{insertSql}; {identitySql}", param, cancellationToken));
    }
}
