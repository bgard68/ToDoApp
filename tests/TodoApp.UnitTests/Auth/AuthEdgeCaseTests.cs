using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Auth.Commands.GoogleSignIn;
using TodoApp.Application.Auth.Commands.Login;
using TodoApp.Application.Auth.Commands.RefreshToken;
using TodoApp.Application.Auth.Commands.Register;
using TodoApp.Application.Auth.Commands.RevokeAllTokens;
using TodoApp.Application.Auth.Commands.RevokeToken;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Models;
using TodoApp.Domain.Entities;
using TodoApp.Infrastructure.Authentication;
using TodoApp.UnitTests.TestSupport;
using Xunit;
using DomainRefreshToken = TodoApp.Domain.Entities.RefreshToken;

namespace TodoApp.UnitTests.Auth;

/// <summary>
/// The rejection and recovery paths of the auth handlers: disabled accounts, replayed or
/// expired refresh tokens, and logout attempts against tokens the caller does not own.
/// </summary>
public class AuthEdgeCaseTests
{
    private readonly FakeJwtTokenService _jwt = new();
    private readonly FakeDateTimeProvider _clock = new();

    private User SeedUser(TestDatabase db, string email = "edge@example.com")
    {
        var user = new User(email, "hash", _clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();
        return user;
    }

    private (string raw, DomainRefreshToken entity) AddToken(
        TestDatabase db, int userId, DateTimeOffset? expiresAt = null)
    {
        var created = _jwt.CreateRefreshToken();
        var entity = new DomainRefreshToken(
            userId, created.TokenHash, expiresAt ?? created.ExpiresAt, _clock.UtcNow);
        db.Context.RefreshTokens.Add(entity);
        db.Context.SaveChanges();
        return (created.RawToken, entity);
    }

    // ---- Refresh -------------------------------------------------------------------

    [Fact]
    public async Task Refresh_WithUnknownToken_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        var handler = new RefreshTokenCommandHandler(db.NewContext(), _jwt, _clock);

        var act = () => handler.Handle(
            new RefreshTokenCommand { RefreshToken = "never-issued" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Refresh_WhenTheOwningUserIsGone_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        db.DisableForeignKeyEnforcement();
        var (raw, _) = AddToken(db, userId: 9999); // no matching Users row
        var handler = new RefreshTokenCommandHandler(db.NewContext(), _jwt, _clock);

        var act = () => handler.Handle(
            new RefreshTokenCommand { RefreshToken = raw }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Refresh_ForADeactivatedUser_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var (raw, _) = AddToken(db, user.Id);

        user.Deactivate(_clock.UtcNow);
        db.Context.SaveChanges();

        var handler = new RefreshTokenCommandHandler(db.NewContext(), _jwt, _clock);
        var act = () => handler.Handle(
            new RefreshTokenCommand { RefreshToken = raw }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Refresh_WithExpiredToken_RevokesItSoAReplayIsDetectable()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var (raw, entity) = AddToken(db, user.Id, expiresAt: _clock.UtcNow.AddMinutes(-1));

        var handler = new RefreshTokenCommandHandler(db.NewContext(), _jwt, _clock);
        var act = () => handler.Handle(
            new RefreshTokenCommand { RefreshToken = raw }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();

        // Left active, an expired row would never trip the reuse detection (review finding L7).
        using var read = db.NewContext();
        var stored = await read.RefreshTokens.SingleAsync(t => t.Id == entity.Id);
        stored.RevokedAt.Should().NotBeNull();
        stored.RevokedReason.Should().Be("Expired");
    }

    // ---- Revoke (logout) -----------------------------------------------------------

    [Fact]
    public async Task Revoke_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new RevokeTokenCommandHandler(
            db.NewContext(), _jwt, new FakeCurrentUserService(), _clock);

        var act = () => handler.Handle(
            new RevokeTokenCommand { RefreshToken = "anything" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Revoke_WithBlankToken_IsANoOp(string token)
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        AddToken(db, user.Id);

        var handler = new RevokeTokenCommandHandler(
            db.NewContext(), _jwt, new FakeCurrentUserService { UserId = user.Id }, _clock);

        await handler.Handle(new RevokeTokenCommand { RefreshToken = token }, CancellationToken.None);

        using var read = db.NewContext();
        (await read.RefreshTokens.CountAsync(t => t.RevokedAt == null)).Should().Be(1);
    }

    [Fact]
    public async Task Revoke_WithAnUnknownToken_IsASilentNoOp()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);

        var handler = new RevokeTokenCommandHandler(
            db.NewContext(), _jwt, new FakeCurrentUserService { UserId = user.Id }, _clock);

        // Must not reveal whether the token exists.
        await handler.Handle(
            new RevokeTokenCommand { RefreshToken = "never-issued" }, CancellationToken.None);
    }

    [Fact]
    public async Task Revoke_DoesNotTouchAnotherUsersToken()
    {
        using var db = new TestDatabase();
        var me = SeedUser(db);
        var other = SeedUser(db, "other@example.com");
        var (theirRaw, theirToken) = AddToken(db, other.Id);

        var handler = new RevokeTokenCommandHandler(
            db.NewContext(), _jwt, new FakeCurrentUserService { UserId = me.Id }, _clock);

        await handler.Handle(new RevokeTokenCommand { RefreshToken = theirRaw }, CancellationToken.None);

        using var read = db.NewContext();
        (await read.RefreshTokens.SingleAsync(t => t.Id == theirToken.Id)).RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Revoke_RevokesTheCallersOwnActiveToken()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var (raw, entity) = AddToken(db, user.Id);

        var handler = new RevokeTokenCommandHandler(
            db.NewContext(), _jwt, new FakeCurrentUserService { UserId = user.Id }, _clock);

        await handler.Handle(new RevokeTokenCommand { RefreshToken = raw }, CancellationToken.None);

        using var read = db.NewContext();
        var stored = await read.RefreshTokens.SingleAsync(t => t.Id == entity.Id);
        stored.RevokedReason.Should().Be("Logout");
    }

    [Fact]
    public async Task Revoke_OnAnAlreadyRevokedToken_LeavesTheOriginalReason()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var (raw, entity) = AddToken(db, user.Id);
        entity.Revoke("Rotated", _clock.UtcNow);
        db.Context.SaveChanges();

        var handler = new RevokeTokenCommandHandler(
            db.NewContext(), _jwt, new FakeCurrentUserService { UserId = user.Id }, _clock);

        await handler.Handle(new RevokeTokenCommand { RefreshToken = raw }, CancellationToken.None);

        using var read = db.NewContext();
        (await read.RefreshTokens.SingleAsync(t => t.Id == entity.Id))
            .RevokedReason.Should().Be("Rotated");
    }

    // ---- Login ---------------------------------------------------------------------

    [Fact]
    public async Task Login_ForADeactivatedAccount_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        var hasher = new PasswordHasher();
        var user = new User("disabled@example.com", hasher.Hash("Password1"), _clock.UtcNow);
        user.Deactivate(_clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();

        var handler = new LoginCommandHandler(db.NewContext(), hasher, _jwt, _clock);
        var act = () => handler.Handle(
            new LoginCommand { Email = "disabled@example.com", Password = "Password1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("This account has been disabled.");
    }

    [Fact]
    public async Task Register_WhenTheInsertIsRejected_ThrowsConflictRatherThan500()
    {
        using var db = new TestDatabase();

        // The pre-check passes, then a concurrent request takes the email before this one saves.
        // A pending row the database will reject reproduces the same DbUpdateException.
        var context = db.NewContext();
        context.ExternalLogins.Add(new ExternalLogin(9999, "Google", "sub-poison", _clock.UtcNow));

        var handler = new RegisterCommandHandler(
            context, new PasswordHasher(), _jwt, _clock, new FakeBreachedPasswordChecker());

        var act = () => handler.Handle(
            new RegisterCommand { Email = "racer@example.com", Password = "Password1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    // ---- Revoke all ----------------------------------------------------------------

    [Fact]
    public async Task RevokeAll_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new RevokeAllTokensCommandHandler(
            db.NewContext(), new FakeCurrentUserService(), _clock);

        var act = () => handler.Handle(new RevokeAllTokensCommand(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task RevokeAll_ForAUserThatDoesNotExist_ThrowsNotFound()
    {
        using var db = new TestDatabase();
        var admin = SeedUser(db, "admin@example.com");

        var handler = new RevokeAllTokensCommandHandler(
            db.NewContext(),
            new FakeCurrentUserService { UserId = admin.Id, Role = "Admin" },
            _clock);

        var act = () => handler.Handle(
            new RevokeAllTokensCommand { UserId = 4242 }, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task RevokeAll_AsAdmin_KillsAnotherUsersSessions()
    {
        using var db = new TestDatabase();
        var admin = SeedUser(db, "admin@example.com");
        var target = SeedUser(db, "target@example.com");
        AddToken(db, target.Id);
        var originalStamp = target.SecurityStamp;

        var handler = new RevokeAllTokensCommandHandler(
            db.NewContext(),
            new FakeCurrentUserService { UserId = admin.Id, Role = "Admin" },
            _clock);

        await handler.Handle(new RevokeAllTokensCommand { UserId = target.Id }, CancellationToken.None);

        using var read = db.NewContext();
        (await read.RefreshTokens.CountAsync(t => t.UserId == target.Id && t.RevokedAt == null))
            .Should().Be(0);
        (await read.Users.SingleAsync(u => u.Id == target.Id))
            .SecurityStamp.Should().NotBe(originalStamp);
    }

    // ---- Google sign-in ------------------------------------------------------------

    [Fact]
    public async Task Google_ForADeactivatedAccount_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        var user = new User("disabled@example.com", "hash", _clock.UtcNow);
        user.Deactivate(_clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();

        var handler = new GoogleSignInCommandHandler(
            db.NewContext(),
            new FakeGoogleTokenValidator
            {
                Result = new GoogleUserInfo("sub-disabled", "disabled@example.com", true, null)
            },
            _jwt,
            _clock);

        var act = () => handler.Handle(new GoogleSignInCommand { IdToken = "t" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>()
            .WithMessage("This account has been disabled.");
    }

    [Fact]
    public async Task Google_WithAnAlreadyLinkedAccount_SignsInWithoutRelinking()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db, "linked@example.com");
        db.Context.ExternalLogins.Add(new ExternalLogin(user.Id, "Google", "sub-linked", _clock.UtcNow));
        db.Context.SaveChanges();

        var handler = new GoogleSignInCommandHandler(
            db.NewContext(),
            new FakeGoogleTokenValidator
            {
                Result = new GoogleUserInfo("sub-linked", "linked@example.com", true, null)
            },
            _jwt,
            _clock);

        var response = await handler.Handle(
            new GoogleSignInCommand { IdToken = "t" }, CancellationToken.None);

        response.User.Id.Should().Be(user.Id);

        using var read = db.NewContext();
        (await read.ExternalLogins.CountAsync()).Should().Be(1);
        (await read.Users.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Google_WhenTheLinkedUserRowIsGone_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        db.DisableForeignKeyEnforcement();
        // An orphaned link: the ExternalLogin row survives but its user does not.
        db.Context.ExternalLogins.Add(new ExternalLogin(9999, "Google", "sub-orphan", _clock.UtcNow));
        db.Context.SaveChanges();

        var handler = new GoogleSignInCommandHandler(
            db.NewContext(),
            new FakeGoogleTokenValidator
            {
                Result = new GoogleUserInfo("sub-orphan", "orphan@example.com", true, null)
            },
            _jwt,
            _clock);

        var act = () => handler.Handle(new GoogleSignInCommand { IdToken = "t" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Google_WhenTheInsertIsRejected_ThrowsConflictRatherThan500()
    {
        using var db = new TestDatabase();

        // Stand in for the concurrent first-time sign-in that grabs the same (Provider,
        // ProviderKey) or Email a moment before this one saves: a pending row the database
        // will reject, so the handler's SaveChanges raises DbUpdateException exactly as the
        // unique-index violation would.
        var context = db.NewContext();
        context.ExternalLogins.Add(new ExternalLogin(9999, "Google", "sub-poison", _clock.UtcNow));

        var handler = new GoogleSignInCommandHandler(
            context,
            new FakeGoogleTokenValidator
            {
                Result = new GoogleUserInfo("sub-race", "racer@example.com", true, null)
            },
            _jwt,
            _clock);

        var act = () => handler.Handle(new GoogleSignInCommand { IdToken = "t" }, CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }
}
