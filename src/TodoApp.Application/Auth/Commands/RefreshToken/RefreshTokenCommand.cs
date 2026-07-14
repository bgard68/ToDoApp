using MediatR;
using TodoApp.Application.Auth.Dtos;

namespace TodoApp.Application.Auth.Commands.RefreshToken;

public record RefreshTokenCommand : IRequest<AuthResponse>
{
    public string RefreshToken { get; init; } = string.Empty;
}
