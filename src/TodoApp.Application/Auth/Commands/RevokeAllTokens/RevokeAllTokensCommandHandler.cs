using MediatR;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;

namespace TodoApp.Application.Auth.Commands.RevokeAllTokens;

public class RevokeAllTokensCommandHandler : IRequestHandler<RevokeAllTokensCommand>
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public RevokeAllTokensCommandHandler(
        IApplicationDbContext db,
        ICurrentUserService currentUser,
        IDateTimeProvider dateTime)
    {
        _db = db;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task Handle(RevokeAllTokensCommand request, CancellationToken cancellationToken)
    {
        var currentUserId = _currentUser.UserId ?? throw new UnauthorizedException();
        var targetUserId = request.UserId ?? currentUserId;

        // Only an admin may revoke sessions for a different user.
        if (targetUserId != currentUserId && !_currentUser.IsInRole(nameof(UserRole.Admin)))
        {
            throw new ForbiddenAccessException();
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == targetUserId, cancellationToken)
            ?? throw new NotFoundException(nameof(User), targetUserId);

        var now = _dateTime.UtcNow;

        // Rotating the stamp invalidates every previously issued access token for this user.
        user.RotateSecurityStamp(now);

        var activeTokens = await _db.RefreshTokens
            .Where(t => t.UserId == targetUserId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var token in activeTokens)
        {
            token.Revoke("All sessions revoked", now);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
