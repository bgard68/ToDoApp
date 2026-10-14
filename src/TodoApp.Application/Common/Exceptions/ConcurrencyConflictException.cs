namespace TodoApp.Application.Common.Exceptions;

/// <summary>
/// Thrown when an optimistic-concurrency check fails (the row was modified or removed by
/// someone else since the client read it). Mapped to HTTP 409; <see cref="CurrentValue"/>
/// carries the latest server state so the client can reload and re-apply.
/// </summary>
/// <remarks>
/// A handler raises this after catching <see cref="ConcurrencyViolationException"/> from the
/// persistence layer and re-reading the current row. Pass that exception as
/// <paramref name="innerException"/> so the provider's error survives for the logs — the 409 is
/// the client's answer, not a reason to discard what actually happened.
/// </remarks>
public class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(
        string message,
        object? currentValue = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        CurrentValue = currentValue;
    }

    public object? CurrentValue { get; }
}
