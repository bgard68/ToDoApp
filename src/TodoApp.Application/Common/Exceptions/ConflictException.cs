namespace TodoApp.Application.Common.Exceptions;

/// <summary>The request conflicts with existing state (e.g. duplicate email). Mapped to HTTP 409.</summary>
public class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }
}
