namespace TodoApp.Application.Common.Exceptions;

/// <summary>
/// A write was rejected because it would duplicate a value the database requires to be unique.
/// </summary>
/// <remarks>
/// The persistence layer raises this in place of the provider's own error, so a handler can catch
/// the one save failure it knows how to explain. Handlers used to catch EF Core's
/// <c>DbUpdateException</c> directly, which is the base type for *every* save failure: a dropped
/// connection, a foreign-key or check violation, and a timeout all arrived as the same type and
/// were reported to the caller as "already exists" — a 409 for a fault that was neither the
/// client's doing nor a conflict, with the original exception discarded so nothing reached the
/// logs either. Narrowing the catch to this type leaves every other failure to propagate as a 500
/// with its cause intact.
/// </remarks>
public class UniqueConstraintViolationException : Exception
{
    public UniqueConstraintViolationException(Exception? innerException = null)
        : base("A uniqueness constraint was violated.", innerException)
    {
    }
}
