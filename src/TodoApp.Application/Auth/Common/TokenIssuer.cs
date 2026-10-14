using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Common;

/// <inheritdoc cref="ITokenIssuer"/>
public class TokenIssuer : ITokenIssuer
{
    private readonly IJwtTokenService _jwt;
    private readonly IApplicationDbContext _db;

    public TokenIssuer(IJwtTokenService jwt, IApplicationDbContext db)
    {
        _jwt = jwt;
        _db = db;
    }

    /// <inheritdoc />
    public AuthResponse Issue(User user, DateTimeOffset now)
    {
        var access = _jwt.CreateAccessToken(user);
        var refresh = _jwt.CreateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken(user.Id, refresh.TokenHash, refresh.ExpiresAt, now));

        return new AuthResponse(
            access.Token,
            access.ExpiresAt,
            refresh.RawToken,
            refresh.ExpiresAt,
            UserDto.FromEntity(user));
    }
}
