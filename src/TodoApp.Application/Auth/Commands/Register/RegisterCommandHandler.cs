using MediatR;
using TodoApp.Application.Auth.Common;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Commands.Register;

public class RegisterCommandHandler : IRequestHandler<RegisterCommand, AuthResponse>
{
    private readonly IUserRepository _users;
    private readonly ICategoryRepository _categories;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly IDateTimeProvider _dateTime;

    public RegisterCommandHandler(
        IUserRepository users,
        ICategoryRepository categories,
        IRefreshTokenRepository refreshTokens,
        IUnitOfWork unitOfWork,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime)
    {
        _users = users;
        _categories = categories;
        _refreshTokens = refreshTokens;
        _unitOfWork = unitOfWork;
        _hasher = hasher;
        _jwt = jwt;
        _dateTime = dateTime;
    }

    public async Task<AuthResponse> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);

        if (await _users.EmailExistsAsync(email, cancellationToken))
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var now = _dateTime.UtcNow;
        var user = new User(email, _hasher.Hash(request.Password), now);

        // Insert the user, seed starter categories, and issue the first token pair atomically.
        // The pre-check above handles the common case; the unique index on Email covers the race
        // where a concurrent request inserts the same email, surfaced as DuplicateKeyException.
        try
        {
            return await _unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await _users.AddAsync(user, ct);
                await _categories.AddRangeAsync(Category.DefaultsFor(user.Id, now), ct);
                return await TokenResponseFactory.IssueAsync(user, _jwt, _refreshTokens, now, ct);
            }, cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            throw new ConflictException("An account with this email already exists.");
        }
    }
}
