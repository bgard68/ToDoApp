namespace TodoApp.Application.Common.Exceptions;

/// <summary>
/// Thrown when an optimistic-concurrency check fails (the row was modified or removed by
/// someone else since the client read it). Mapped to HTTP 409; <see cref="CurrentValue"/>
/// carries the latest server state so the client can reload and re-apply.
/// </summary>
public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message, object? currentValue = null)
        : base(message)
    {
        CurrentValue = currentValue;
    }

    public object? CurrentValue { get; }
}
