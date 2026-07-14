using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Common;

/// <summary>
/// Shared helper that issues an access token plus a new refresh token for a user and
/// stages the refresh token for persistence. The caller is responsible for SaveChanges.
/// </summary>
internal static class TokenResponseFactory
{
    public static AuthResponse Issue(User user, IJwtTokenService jwt, IApplicationDbContext db, DateTimeOffset now)
    {
        var access = jwt.CreateAccessToken(user);
        var refresh = jwt.CreateRefreshToken();

        db.RefreshTokens.Add(new RefreshToken(user.Id, refresh.TokenHash, refresh.ExpiresAt, now));

        return new AuthResponse(
            access.Token,
            access.ExpiresAt,
            refresh.RawToken,
            refresh.ExpiresAt,
            UserDto.FromEntity(user));
    }
}
