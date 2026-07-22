namespace TodoApp.Application.Common.Interfaces;

/// <summary>
/// Runs several writes inside a single database transaction. The multi-write command
/// handlers (register, external sign-in, refresh-token rotation) previously relied on one
/// EF <c>SaveChanges</c> call as their atomic unit; with Dapper that atomicity is explicit.
/// </summary>
public interface IUnitOfWork
{
    Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken);

    Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken);
}
