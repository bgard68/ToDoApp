using MediatR;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;

namespace TodoApp.Application.Auth.Commands.RevokeToken;

public class RevokeTokenCommandHandler : IRequestHandler<RevokeTokenCommand>
{
    private readonly IApplicationDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public RevokeTokenCommandHandler(
        IApplicationDbContext db,
        IJwtTokenService jwt,
        ICurrentUserService currentUser,
        IDateTimeProvider dateTime)
    {
        _db = db;
        _jwt = jwt;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task Handle(RevokeTokenCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return;
        }

        var hash = _jwt.HashToken(request.RefreshToken);
        var token = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        // Only the owner may revoke; don't reveal whether the token exists otherwise.
        if (token is null || token.UserId != userId)
        {
            return;
        }

        var now = _dateTime.UtcNow;
        if (token.IsActive(now))
        {
            token.Revoke("Logout", now);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
