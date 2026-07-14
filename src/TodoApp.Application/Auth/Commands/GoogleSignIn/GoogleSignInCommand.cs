using MediatR;
using TodoApp.Application.Auth.Dtos;

namespace TodoApp.Application.Auth.Commands.GoogleSignIn;

/// <summary>Signs in (or registers) a user from a Google ID token obtained on the client.</summary>
public record GoogleSignInCommand : IRequest<AuthResponse>
{
    public string IdToken { get; init; } = string.Empty;
}
