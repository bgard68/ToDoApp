using MediatR;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Auth.Common;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Common.Models;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Commands.GoogleSignIn;

public class GoogleSignInCommandHandler : IRequestHandler<GoogleSignInCommand, AuthResponse>
{
    private const string Provider = "Google";

    private readonly IApplicationDbContext _db;
    private readonly IGoogleTokenValidator _google;
    private readonly IJwtTokenService _jwt;
    private readonly IDateTimeProvider _dateTime;

    public GoogleSignInCommandHandler(
        IApplicationDbContext db,
        IGoogleTokenValidator google,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime)
    {
        _db = db;
        _google = google;
        _jwt = jwt;
        _dateTime = dateTime;
    }

    public async Task<AuthResponse> Handle(GoogleSignInCommand request, CancellationToken cancellationToken)
    {
        var payload = await _google.ValidateAsync(request.IdToken, cancellationToken)
            ?? throw new UnauthorizedException("Invalid Google token.");

        if (!payload.EmailVerified)
        {
            throw new UnauthorizedException("Your Google email address is not verified.");
        }

        try
        {
            var user = await ResolveUserAsync(payload, cancellationToken);

            if (!user.IsActive)
            {
                throw new UnauthorizedException("This account has been disabled.");
            }

            var response = TokenResponseFactory.Issue(user, _jwt, _db, _dateTime.UtcNow);
            await _db.SaveChangesAsync(cancellationToken);

            return response;
        }
        catch (DbUpdateException)
        {
            // A concurrent first-time sign-in created the same user/external login (unique
            // index on Email or (Provider, ProviderKey)). Ask the client to retry.
            throw new ConflictException("This sign-in conflicted with a concurrent request. Please try again.");
        }
    }

    private async Task<User> ResolveUserAsync(GoogleUserInfo payload, CancellationToken cancellationToken)
    {
        // 1) Already linked to this Google account.
        var login = await _db.ExternalLogins
            .FirstOrDefaultAsync(l => l.Provider == Provider && l.ProviderKey == payload.Subject, cancellationToken);

        if (login is not null)
        {
            return await _db.Users.FirstOrDefaultAsync(u => u.Id == login.UserId, cancellationToken)
                ?? throw new UnauthorizedException("Invalid Google token.");
        }

        // 2) Otherwise link to an existing local account with the same email, or create one.
        var email = User.NormalizeEmail(payload.Email);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);

        var now = _dateTime.UtcNow;
        if (user is null)
        {
            user = User.CreateExternal(email, now);
            _db.Users.Add(user);
            await _db.SaveChangesAsync(cancellationToken); // obtain the user's Id for the FK

            _db.Categories.AddRange(Category.DefaultsFor(user.Id, now));
        }

        _db.ExternalLogins.Add(new ExternalLogin(user.Id, Provider, payload.Subject, now));
        return user;
    }
}
