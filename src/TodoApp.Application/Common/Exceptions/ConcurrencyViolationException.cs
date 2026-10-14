namespace TodoApp.Application.Common.Exceptions;

/// <summary>
/// A write was rejected because the row it targeted had already moved on — the UPDATE or DELETE
/// matched zero rows, because someone else changed or removed it since this request read it.
/// </summary>
/// <remarks>
/// <para>
/// The persistence layer raises this in place of EF Core's <c>DbUpdateConcurrencyException</c>, for
/// the same reason it raises <see cref="UniqueConstraintViolationException"/> in place of
/// <c>DbUpdateException</c>: a handler should be able to recognise the one save failure it knows
/// how to explain without naming an ORM type. Keeping both translations in the context override
/// means no overload can bypass them.
/// </para>
/// <para>
/// This is the low-level signal, not the HTTP answer. It carries no payload, because the context
/// cannot know which DTO shape the caller wants. A handler catches it, re-reads the current row,
/// and throws <see cref="ConcurrencyConflictException"/> — which is the one mapped to 409 and
/// which carries the fresh server state the client needs in order to reload and re-apply.
/// </para>
/// </remarks>
public class ConcurrencyViolationException : Exception
{
    public ConcurrencyViolationException(Exception? innerException = null)
        : base("The row was modified or removed by someone else.", innerException)
    {
    }
}
