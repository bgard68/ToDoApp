using FluentAssertions;
using TodoApp.Application.Auth.Commands.RefreshToken;
using TodoApp.Application.Auth.Commands.RevokeAllTokens;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Domain.Entities;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Auth;

public class RefreshAndRevokeTests
{
    private readonly FakeJwtTokenService _jwt = new();
    private readonly FakeDateTimeProvider _clock = new();

    private async Task<User> SeedUserAsync(TestDatabase db)
    {
        var user = new User("user@example.com", "hash", _clock.UtcNow);
        await db.Users.AddAsync(user, CancellationToken.None);
        return user;
    }

    private async Task<(string raw, RefreshToken entity)> AddActiveTokenAsync(TestDatabase db, int userId)
    {
        var created = _jwt.CreateRefreshToken();
        var entity = new RefreshToken(userId, created.TokenHash, created.ExpiresAt, _clock.UtcNow);
        await db.RefreshTokens.AddAsync(entity, CancellationToken.None);
        return (created.RawToken, entity);
    }

    private RefreshTokenCommandHandler Refresh(TestDatabase db) =>
        new(db.Users, db.RefreshTokens, db.UnitOfWork, _jwt, _clock);

    [Fact]
    public async Task Refresh_WithValidToken_RotatesAndRevokesOld()
    {
        using var db = new TestDatabase();
        var user = await SeedUserAsync(db);
        var (raw, _) = await AddActiveTokenAsync(db, user.Id);

        var response = await Refresh(db).Handle(
            new RefreshTokenCommand { RefreshToken = raw }, CancellationToken.None);

        response.RefreshToken.Should().NotBe(raw);

        (await db.CountAsync("RefreshTokens")).Should().Be(2);
        // Exactly one active (the new one).
        (await db.RefreshTokens.GetUnrevokedForUserAsync(user.Id, CancellationToken.None)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Refresh_WithRevokedToken_RevokesAllSessionsAndThrows()
    {
        using var db = new TestDatabase();
        var user = await SeedUserAsync(db);

        // A previously-rotated (revoked) token that an attacker replays...
        var reused = _jwt.CreateRefreshToken();
        var revoked = new RefreshToken(user.Id, reused.TokenHash, reused.ExpiresAt, _clock.UtcNow);
        revoked.Revoke("Rotated", _clock.UtcNow);
        await db.RefreshTokens.AddAsync(revoked, CancellationToken.None);
        // ...plus a currently-active session that must be killed by the reuse response.
        await AddActiveTokenAsync(db, user.Id);
        var originalStamp = user.SecurityStamp;

        var act = () => Refresh(db).Handle(
            new RefreshTokenCommand { RefreshToken = reused.RawToken }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();

        (await db.RefreshTokens.GetUnrevokedForUserAsync(user.Id, CancellationToken.None)).Should().BeEmpty();
        (await db.Users.GetByIdAsync(user.Id, CancellationToken.None))!.SecurityStamp.Should().NotBe(originalStamp);
    }

    [Fact]
    public async Task RevokeAll_RotatesStampAndRevokesEveryToken()
    {
        using var db = new TestDatabase();
        var user = await SeedUserAsync(db);
        await AddActiveTokenAsync(db, user.Id);
        await AddActiveTokenAsync(db, user.Id);
        var originalStamp = user.SecurityStamp;

        var current = new FakeCurrentUserService { UserId = user.Id, Role = "User" };
        var handler = new RevokeAllTokensCommandHandler(db.Users, db.RefreshTokens, db.UnitOfWork, current, _clock);

        await handler.Handle(new RevokeAllTokensCommand(), CancellationToken.None);

        (await db.RefreshTokens.GetUnrevokedForUserAsync(user.Id, CancellationToken.None)).Should().BeEmpty();
        (await db.Users.GetByIdAsync(user.Id, CancellationToken.None))!.SecurityStamp.Should().NotBe(originalStamp);
    }

    [Fact]
    public async Task RevokeAll_ForAnotherUser_AsNonAdmin_ThrowsForbidden()
    {
        using var db = new TestDatabase();
        var me = await SeedUserAsync(db);
        var other = new User("other@example.com", "hash", _clock.UtcNow);
        await db.Users.AddAsync(other, CancellationToken.None);

        var current = new FakeCurrentUserService { UserId = me.Id, Role = "User" };
        var handler = new RevokeAllTokensCommandHandler(db.Users, db.RefreshTokens, db.UnitOfWork, current, _clock);
        var act = () => handler.Handle(
            new RevokeAllTokensCommand { UserId = other.Id }, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }
}
