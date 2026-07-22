using System.Data.Common;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Scoped owner of one open connection (and at most one live transaction) per DI scope.
/// Lazily opens on first use and disposes the connection/transaction when the scope ends.
/// </summary>
public sealed class DbConnectionContext : IDbConnectionContext
{
    private readonly IDbConnectionFactory _factory;
    private DbConnection? _connection;
    private DbTransaction? _transaction;

    public DbConnectionContext(IDbConnectionFactory factory) => _factory = factory;

    public DbTransaction? Transaction => _transaction;

    public async ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        _connection ??= await _factory.CreateOpenAsync(cancellationToken);
        return _connection;
    }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already in progress for this scope.");
        }

        var connection = await GetConnectionAsync(cancellationToken);
        _transaction = await connection.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.CommitAsync(cancellationToken);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_transaction is null)
        {
            return;
        }

        await _transaction.RollbackAsync(cancellationToken);
        await _transaction.DisposeAsync();
        _transaction = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    // Sync disposal for scopes disposed synchronously (e.g. the startup seed scope).
    public void Dispose()
    {
        _transaction?.Dispose();
        _connection?.Dispose();
    }
}
