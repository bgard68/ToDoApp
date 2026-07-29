using MediatR;
using TodoApp.Application.Auth.Common;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Commands.Login;

public class LoginCommandHandler : IRequestHandler<LoginCommand, AuthResponse>
{
    // Computed once from the live hasher so the decoy always costs exactly what a real
    // verification costs, even if the iteration count changes. The hasher is a stateless
    // singleton; a benign race just computes this twice.
    private static string? _dummyHash;

    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly IDateTimeProvider _dateTime;

    public LoginCommandHandler(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _hasher = hasher;
        _jwt = jwt;
        _dateTime = dateTime;
    }

    private string DummyHash => _dummyHash ??= _hasher.Hash("login-timing-equalisation-placeholder");

    public async Task<AuthResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);

        var user = await _users.GetByEmailAsync(email, cancellationToken);

        // Accounts created via an external provider (e.g. Google) have no local password.
        bool passwordOk;
        if (user?.PasswordHash is not null)
        {
            passwordOk = _hasher.Verify(user.PasswordHash, request.Password);
        }
        else
        {
            // Equalise timing. Skipping verification here would return in microseconds for an
            // unknown email versus ~100ms for a known one — a reliable account-enumeration
            // oracle (review finding L4). Burn the same PBKDF2 work and discard the result.
            _hasher.Verify(DummyHash, request.Password);
            passwordOk = false;
        }

        if (user is null || !passwordOk)
        {
            throw new UnauthorizedException("Invalid email or password.");
        }

        if (!user.IsActive)
        {
            throw new UnauthorizedException("This account has been disabled.");
        }

        // Transparent upgrade: the password is already verified, so re-hash it at the current
        // work factor if the stored hash predates a policy change (review finding L8). Accounts
        // migrate as people sign in — no bulk migration, no forced reset.
        if (_hasher.NeedsRehash(user.PasswordHash!))
        {
            user.UpgradePasswordHash(_hasher.Hash(request.Password), _dateTime.UtcNow);
            await _users.UpdateAsync(user, cancellationToken);
        }

        return await TokenResponseFactory.IssueAsync(user, _jwt, _refreshTokens, _dateTime.UtcNow, cancellationToken);
    }
}
