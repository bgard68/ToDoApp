using MediatR;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;

namespace TodoApp.Application.Auth.Commands.RevokeAllTokens;

public class RevokeAllTokensCommandHandler : IRequestHandler<RevokeAllTokensCommand>
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public RevokeAllTokensCommandHandler(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IUnitOfWork unitOfWork,
        ICurrentUserService currentUser,
        IDateTimeProvider dateTime)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _unitOfWork = unitOfWork;
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

        var user = await _users.GetByIdAsync(targetUserId, cancellationToken)
            ?? throw new NotFoundException(nameof(User), targetUserId);

        var now = _dateTime.UtcNow;

        // Rotating the stamp invalidates every previously issued access token for this user.
        user.RotateSecurityStamp(now);

        await _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await _users.UpdateAsync(user, ct);

            var activeTokens = await _refreshTokens.GetUnrevokedForUserAsync(targetUserId, ct);
            foreach (var token in activeTokens)
            {
                token.Revoke("All sessions revoked", now);
                await _refreshTokens.UpdateAsync(token, ct);
            }
        }, cancellationToken);
    }
}
