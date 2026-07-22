namespace TodoApp.Application.Common.Exceptions;

/// <summary>
/// A write violated a unique constraint (e.g. duplicate email or category name). The persistence
/// layer throws this provider-neutral exception so handlers can translate it into a domain
/// <see cref="ConflictException"/> without referencing a specific database provider's types.
/// </summary>
public class DuplicateKeyException : Exception
{
    public DuplicateKeyException(string? message = null, Exception? innerException = null)
        : base(message ?? "A unique constraint was violated.", innerException)
    {
    }
}
