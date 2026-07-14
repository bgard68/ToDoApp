using MediatR;

namespace TodoApp.Application.Auth.Commands.RevokeToken;

/// <summary>Logout: revokes a single refresh token belonging to the current user.</summary>
public record RevokeTokenCommand : IRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}
