using System.Data.Common;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Holds a single open connection for the lifetime of a DI scope (mirrors EF's scoped
/// DbContext) plus the ambient transaction, if a unit of work has started one. Repositories
/// read <see cref="GetConnectionAsync"/> and <see cref="Transaction"/> so their Dapper
/// commands all run on the same connection and enlist in the same transaction.
/// </summary>
public interface IDbConnectionContext : IAsyncDisposable, IDisposable
{
    /// <summary>Returns the scope's connection, opening it on first use.</summary>
    ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken);

    /// <summary>The current transaction, or null when running in autocommit mode.</summary>
    DbTransaction? Transaction { get; }

    Task BeginTransactionAsync(CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
