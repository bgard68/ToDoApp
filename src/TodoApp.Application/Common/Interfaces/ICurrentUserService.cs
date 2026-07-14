namespace TodoApp.Application.Common.Interfaces;

/// <summary>
/// Exposes the authenticated user for the current request. Implemented in the
/// WebApi/Infrastructure layer over the HTTP context.
/// </summary>
public interface ICurrentUserService
{
    int? UserId { get; }

    string? Email { get; }

    bool IsAuthenticated { get; }

    bool IsInRole(string role);
}
