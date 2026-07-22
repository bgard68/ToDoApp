using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Infrastructure.Persistence;
using TodoApp.Infrastructure.Persistence.Dapper;
using TodoApp.Infrastructure.Persistence.Repositories;

namespace TodoApp.UnitTests.TestSupport;

/// <summary>
/// A Dapper-backed test database over an in-memory SQLite connection (kept alive for the
/// harness lifetime by holding the connection open). The schema is built by the real
/// <see cref="SchemaInitializer"/>, and the repositories + unit of work are wired to the shared
/// connection so handlers execute against real SQL — the same code path as production.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDatabase()
    {
        DapperConfig.Register();

        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();

        var factory = new SharedConnectionFactory(_connection);
        var context = new SharedConnectionContext(_connection);

        new SchemaInitializer(context, factory).EnsureCreatedAsync(default).GetAwaiter().GetResult();

        Todos = new TodoRepository(context, factory);
        Categories = new CategoryRepository(context, factory);
        Users = new UserRepository(context, factory);
        RefreshTokens = new RefreshTokenRepository(context, factory);
        ExternalLogins = new ExternalLoginRepository(context, factory);
        UnitOfWork = new UnitOfWork(context);
    }

    public ITodoRepository Todos { get; }
    public ICategoryRepository Categories { get; }
    public IUserRepository Users { get; }
    public IRefreshTokenRepository RefreshTokens { get; }
    public IExternalLoginRepository ExternalLogins { get; }
    public IUnitOfWork UnitOfWork { get; }

    /// <summary>Row count of a table — for persistence assertions.</summary>
    public Task<int> CountAsync(string table) =>
        _connection.ExecuteScalarAsync<int>($"SELECT COUNT(1) FROM {table}");

    public void Dispose() => _connection.Dispose();
}

/// <summary>Connection factory that always hands back the harness's single shared connection.</summary>
internal sealed class SharedConnectionFactory : IDbConnectionFactory
{
    private readonly DbConnection _connection;

    public SharedConnectionFactory(DbConnection connection) => _connection = connection;

    public DbProvider Provider => DbProvider.Sqlite;

    public DbConnection Create() => _connection;

    public Task<DbConnection> CreateOpenAsync(CancellationToken cancellationToken) => Task.FromResult(_connection);
}

/// <summary>
/// Connection context over the shared connection. Unlike the production context it never
/// disposes the connection (the in-memory database would vanish) — <see cref="TestDatabase"/>
/// owns its lifetime.
/// </summary>
internal sealed class SharedConnectionContext : IDbConnectionContext
{
    private readonly DbConnection _connection;
    private DbTransaction? _transaction;

    public SharedConnectionContext(DbConnection connection) => _connection = connection;

    public DbTransaction? Transaction => _transaction;

    public ValueTask<DbConnection> GetConnectionAsync(CancellationToken cancellationToken) => new(_connection);

    public async Task BeginTransactionAsync(CancellationToken cancellationToken)
        => _transaction = await _connection.BeginTransactionAsync(cancellationToken);

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

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        // The connection is owned by TestDatabase.
    }
}
