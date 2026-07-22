using MediatR;
using TodoApp.Application.Auth.Common;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Commands.Login;

public class LoginCommandHandler : IRequestHandler<LoginCommand, AuthResponse>
{
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

    public async Task<AuthResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);

        var user = await _users.GetByEmailAsync(email, cancellationToken);

        // Accounts created via an external provider (e.g. Google) have no local password.
        var passwordOk = user?.PasswordHash is not null
            && _hasher.Verify(user.PasswordHash, request.Password);

        if (user is null || !passwordOk)
        {
            throw new UnauthorizedException("Invalid email or password.");
        }

        if (!user.IsActive)
        {
            throw new UnauthorizedException("This account has been disabled.");
        }

        return await TokenResponseFactory.IssueAsync(user, _jwt, _refreshTokens, _dateTime.UtcNow, cancellationToken);
    }
}
