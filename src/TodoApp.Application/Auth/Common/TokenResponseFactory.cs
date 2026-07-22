using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Common;

/// <summary>
/// Shared helper that issues an access token plus a new refresh token for a user and persists
/// the refresh token. Call within the caller's unit of work when it is part of a larger
/// multi-write flow (register, external sign-in).
/// </summary>
internal static class TokenResponseFactory
{
    public static async Task<AuthResponse> IssueAsync(
        User user,
        IJwtTokenService jwt,
        IRefreshTokenRepository refreshTokens,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var access = jwt.CreateAccessToken(user);
        var refresh = jwt.CreateRefreshToken();

        await refreshTokens.AddAsync(
            new RefreshToken(user.Id, refresh.TokenHash, refresh.ExpiresAt, now), cancellationToken);

        return new AuthResponse(
            access.Token,
            access.ExpiresAt,
            refresh.RawToken,
            refresh.ExpiresAt,
            UserDto.FromEntity(user));
    }
}
