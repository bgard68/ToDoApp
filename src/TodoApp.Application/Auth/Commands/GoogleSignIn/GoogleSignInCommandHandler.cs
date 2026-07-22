using MediatR;
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

    private readonly IUserRepository _users;
    private readonly ICategoryRepository _categories;
    private readonly IExternalLoginRepository _externalLogins;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IGoogleTokenValidator _google;
    private readonly IJwtTokenService _jwt;
    private readonly IDateTimeProvider _dateTime;

    public GoogleSignInCommandHandler(
        IUserRepository users,
        ICategoryRepository categories,
        IExternalLoginRepository externalLogins,
        IRefreshTokenRepository refreshTokens,
        IUnitOfWork unitOfWork,
        IGoogleTokenValidator google,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime)
    {
        _users = users;
        _categories = categories;
        _externalLogins = externalLogins;
        _refreshTokens = refreshTokens;
        _unitOfWork = unitOfWork;
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
            // Resolve/create the account, link the external login, and issue tokens atomically.
            return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                var user = await ResolveUserAsync(payload, ct);

                if (!user.IsActive)
                {
                    throw new UnauthorizedException("This account has been disabled.");
                }

                return await TokenResponseFactory.IssueAsync(user, _jwt, _refreshTokens, _dateTime.UtcNow, ct);
            }, cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            // A concurrent first-time sign-in created the same user/external login (unique
            // index on Email or (Provider, ProviderKey)). Ask the client to retry.
            throw new ConflictException("This sign-in conflicted with a concurrent request. Please try again.");
        }
    }

    private async Task<User> ResolveUserAsync(GoogleUserInfo payload, CancellationToken cancellationToken)
    {
        // 1) Already linked to this Google account.
        var login = await _externalLogins.GetByProviderKeyAsync(Provider, payload.Subject, cancellationToken);
        if (login is not null)
        {
            return await _users.GetByIdAsync(login.UserId, cancellationToken)
                ?? throw new UnauthorizedException("Invalid Google token.");
        }

        // 2) Otherwise link to an existing local account with the same email, or create one.
        var email = User.NormalizeEmail(payload.Email);
        var user = await _users.GetByEmailAsync(email, cancellationToken);

        var now = _dateTime.UtcNow;
        if (user is null)
        {
            user = User.CreateExternal(email, now);
            await _users.AddAsync(user, cancellationToken); // assigns the user's Id for the FK
            await _categories.AddRangeAsync(Category.DefaultsFor(user.Id, now), cancellationToken);
        }

        await _externalLogins.AddAsync(new ExternalLogin(user.Id, Provider, payload.Subject, now), cancellationToken);
        return user;
    }
}
