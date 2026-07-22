using TodoApp.Application.Common.Interfaces;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Wraps a delegate in a transaction on the scope's shared connection: begin, run, commit,
/// or roll back on any exception. Repositories invoked inside the delegate pick up the same
/// connection and transaction through <see cref="IDbConnectionContext"/>.
/// </summary>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly IDbConnectionContext _context;

    public UnitOfWork(IDbConnectionContext context) => _context = context;

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _context.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action(cancellationToken);
            await _context.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await _context.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
        => ExecuteInTransactionAsync<object?>(async ct =>
        {
            await action(ct);
            return null;
        }, cancellationToken);
}
