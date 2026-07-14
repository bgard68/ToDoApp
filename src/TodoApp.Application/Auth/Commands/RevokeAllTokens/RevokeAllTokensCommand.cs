using MediatR;

namespace TodoApp.Application.Auth.Commands.RevokeAllTokens;

/// <summary>
/// "Sign out everywhere" / compromise response. Rotates the target user's security
/// stamp (invalidating all access tokens immediately) and revokes all refresh tokens.
/// A user may revoke their own; an Admin may target any <see cref="UserId"/>.
/// </summary>
public record RevokeAllTokensCommand : IRequest
{
    public int? UserId { get; init; }
}
