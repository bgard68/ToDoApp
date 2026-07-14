using MediatR;
using TodoApp.Application.Auth.Dtos;

namespace TodoApp.Application.Auth.Commands.Login;

public record LoginCommand : IRequest<AuthResponse>
{
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
