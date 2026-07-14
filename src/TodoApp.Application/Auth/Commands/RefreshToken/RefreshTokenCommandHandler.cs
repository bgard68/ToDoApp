using MediatR;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using DomainRefreshToken = TodoApp.Domain.Entities.RefreshToken;

namespace TodoApp.Application.Auth.Commands.RefreshToken;

public class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, AuthResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IDateTimeProvider _dateTime;

    public RefreshTokenCommandHandler(IApplicationDbContext db, IJwtTokenService jwt, IDateTimeProvider dateTime)
    {
        _db = db;
        _jwt = jwt;
        _dateTime = dateTime;
    }

    public async Task<AuthResponse> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var now = _dateTime.UtcNow;
        var hash = _jwt.HashToken(request.RefreshToken);

        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (token is null)
        {
            throw new UnauthorizedException("Invalid refresh token.");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == token.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("Invalid refresh token.");
        }

        // Presenting an already-revoked token means it was rotated away (or stolen and replayed).
        // Treat as compromise: rotate the security stamp and revoke every outstanding token.
        if (token.IsRevoked)
        {
            user.RotateSecurityStamp(now);
            await RevokeAllActiveTokensAsync(user.Id, "Refresh token reuse detected", now, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedException("Refresh token has been revoked.");
        }

        if (token.IsExpired(now))
        {
            throw new UnauthorizedException("Refresh token has expired.");
        }

        // Rotate: revoke the presented token and issue a fresh pair.
        var access = _jwt.CreateAccessToken(user);
        var newRefresh = _jwt.CreateRefreshToken();

        token.Revoke("Rotated", now, newRefresh.TokenHash);
        _db.RefreshTokens.Add(new DomainRefreshToken(user.Id, newRefresh.TokenHash, newRefresh.ExpiresAt, now));

        await _db.SaveChangesAsync(cancellationToken);

        return new AuthResponse(
            access.Token,
            access.ExpiresAt,
            newRefresh.RawToken,
            newRefresh.ExpiresAt,
            UserDto.FromEntity(user));
    }

    private async Task RevokeAllActiveTokensAsync(int userId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var t in active)
        {
            t.Revoke(reason, now);
        }
    }
}
