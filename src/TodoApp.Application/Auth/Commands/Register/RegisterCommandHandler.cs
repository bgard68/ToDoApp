using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Auth.Common;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Auth.Commands.Register;

public class RegisterCommandHandler : IRequestHandler<RegisterCommand, AuthResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly IDateTimeProvider _dateTime;
    private readonly IBreachedPasswordChecker _breachChecker;

    public RegisterCommandHandler(
        IApplicationDbContext db,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IDateTimeProvider dateTime,
        IBreachedPasswordChecker breachChecker)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
        _dateTime = dateTime;
        _breachChecker = breachChecker;
    }

    public async Task<AuthResponse> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var email = User.NormalizeEmail(request.Email);

        // Reject passwords known to appear in breach corpora (review finding L9). Composition
        // rules alone stop nothing that matters — "Password1" passes every one of them. The
        // checker fails open, so an outage at the lookup service never blocks registration.
        if (await _breachChecker.IsBreachedAsync(request.Password, cancellationToken))
        {
            throw new ValidationException(
            [
                new ValidationFailure(
                    nameof(RegisterCommand.Password),
                    "This password has appeared in a known data breach. Please choose a different one.")
            ]);
        }

        if (await _db.Users.AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new ConflictException("An account with this email already exists.");
        }

        var now = _dateTime.UtcNow;
        var user = new User(email, _hasher.Hash(request.Password), now);
        _db.Users.Add(user);

        // Persist first so the user has an Id for the refresh token's foreign key.
        // The pre-check above handles the common case; this catch covers the race where a
        // concurrent request inserts the same email between the check and here (the unique
        // index on Email then rejects this insert).
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException("An account with this email already exists.");
        }

        // Give the new user a starter set of categories to rename/recolor/delete.
        _db.Categories.AddRange(Category.DefaultsFor(user.Id, now));

        var response = TokenResponseFactory.Issue(user, _jwt, _db, now);
        await _db.SaveChangesAsync(cancellationToken);

        return response;
    }
}
