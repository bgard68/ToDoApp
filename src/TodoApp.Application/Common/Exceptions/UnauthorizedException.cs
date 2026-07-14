namespace TodoApp.Application.Common.Exceptions;

/// <summary>Authentication failed or credentials/token are invalid. Mapped to HTTP 401.</summary>
public class UnauthorizedException : Exception
{
    public UnauthorizedException(string message = "Authentication is required.")
        : base(message)
    {
    }
}
