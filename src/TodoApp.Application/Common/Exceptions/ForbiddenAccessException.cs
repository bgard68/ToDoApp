namespace TodoApp.Application.Common.Exceptions;

/// <summary>The caller is authenticated but not allowed to perform the action. Mapped to HTTP 403.</summary>
public class ForbiddenAccessException : Exception
{
    public ForbiddenAccessException(string message = "You do not have permission to perform this action.")
        : base(message)
    {
    }
}
