using MediatR;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using DomainRefreshToken = TodoApp.Domain.Entities.RefreshToken;

namespace TodoApp.Application.Auth.Commands.RefreshToken;

public class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, AuthResponse>
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IJwtTokenService _jwt;
    private readonly IDateTimeProvider _dateTime;

    public RefreshTokenCommandHandler(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IUnitOfWork unitOfWork,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _unitOfWork = unitOfWork;
        _jwt = jwt;
        _dateTime = dateTime;
    }

    public async Task<AuthResponse> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var now = _dateTime.UtcNow;
        var hash = _jwt.HashToken(request.RefreshToken);

        var token = await _refreshTokens.GetByHashAsync(hash, cancellationToken);
        if (token is null)
        {
            throw new UnauthorizedException("Invalid refresh token.");
        }

        var user = await _users.GetByIdAsync(token.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("Invalid refresh token.");
        }

        // Presenting an already-revoked token means it was rotated away (or stolen and replayed).
        // Treat as compromise: rotate the security stamp and revoke every outstanding token.
        if (token.IsRevoked)
        {
            user.RotateSecurityStamp(now);
            await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await _users.UpdateAsync(user, ct);
                await RevokeAllUnrevokedAsync(user.Id, "Refresh token reuse detected", now, ct);
            }, cancellationToken);

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

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _refreshTokens.UpdateAsync(token, ct);
            await _refreshTokens.AddAsync(
                new DomainRefreshToken(user.Id, newRefresh.TokenHash, newRefresh.ExpiresAt, now), ct);
        }, cancellationToken);

        return new AuthResponse(
            access.Token,
            access.ExpiresAt,
            newRefresh.RawToken,
            newRefresh.ExpiresAt,
            UserDto.FromEntity(user));
    }

    private async Task RevokeAllUnrevokedAsync(int userId, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = await _refreshTokens.GetUnrevokedForUserAsync(userId, cancellationToken);
        foreach (var t in active)
        {
            t.Revoke(reason, now);
            await _refreshTokens.UpdateAsync(t, cancellationToken);
        }
    }
}
