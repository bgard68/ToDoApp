using MediatR;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Auth.Common;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Commands.Login;

public class LoginCommandHandler : IRequestHandler<LoginCommand, AuthResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly IDateTimeProvider _dateTime;
    private readonly ITokenIssuer _tokenIssuer;

    public LoginCommandHandler(
        IApplicationDbContext db,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime,
        ITokenIssuer tokenIssuer)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
        _dateTime = dateTime;
        _tokenIssuer = tokenIssuer;
    }

    public async Task<AuthResponse> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        // Accounts created via an external provider (e.g. Google) have no local password.
        bool passwordOk;
        if (user?.PasswordHash is not null)
        {
            passwordOk = _hasher.Verify(user.PasswordHash, request.Password);
        }
        else
        {
            // Equalise timing: burn the same PBKDF2 work an existing account would cost, so an
            // unknown email cannot be told from a known one by how long the answer takes
            // (review finding L4).
            _hasher.VerifyDecoy(request.Password);
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
        }

        var response = _tokenIssuer.Issue(user, _dateTime.UtcNow);
        await _db.SaveChangesAsync(cancellationToken);

        return response;
    }
}
