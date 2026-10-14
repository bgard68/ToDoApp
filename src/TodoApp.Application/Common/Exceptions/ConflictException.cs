namespace TodoApp.Application.Common.Exceptions;

/// <summary>The request conflicts with existing state (e.g. duplicate email). Mapped to HTTP 409.</summary>
public class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Keeps the underlying database error as the inner exception. The caller only needs the
    /// message, but a 409 that turns out to be something else is far easier to diagnose when the
    /// provider's own error is still attached.
    /// </summary>
    public ConflictException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
