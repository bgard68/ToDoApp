using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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

    private User SeedUser(TestDatabase db)
    {
        var user = new User("user@example.com", "hash", _clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();
        return user;
    }

    private (string raw, RefreshToken entity) AddActiveToken(TestDatabase db, int userId)
    {
        var created = _jwt.CreateRefreshToken();
        var entity = new RefreshToken(userId, created.TokenHash, created.ExpiresAt, _clock.UtcNow);
        db.Context.RefreshTokens.Add(entity);
        db.Context.SaveChanges();
        return (created.RawToken, entity);
    }

    [Fact]
    public async Task Refresh_WithValidToken_RotatesAndRevokesOld()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var (raw, _) = AddActiveToken(db, user.Id);

        var handler = new RefreshTokenCommandHandler(db.NewContext(), _jwt, _clock);
        var response = await handler.Handle(
            new RefreshTokenCommand { RefreshToken = raw }, CancellationToken.None);

        response.RefreshToken.Should().NotBe(raw);

        using var read = db.NewContext();
        var tokens = await read.RefreshTokens.ToListAsync();
        tokens.Should().HaveCount(2);
        tokens.Count(t => t.RevokedAt == null).Should().Be(1); // exactly one active (the new one)
    }

    [Fact]
    public async Task Refresh_WithRevokedToken_RevokesAllSessionsAndThrows()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);

        // A previously-rotated (revoked) token that an attacker replays...
        var reused = _jwt.CreateRefreshToken();
        var revoked = new RefreshToken(user.Id, reused.TokenHash, reused.ExpiresAt, _clock.UtcNow);
        revoked.Revoke("Rotated", _clock.UtcNow);
        db.Context.RefreshTokens.Add(revoked);
        // ...plus a currently-active session that must be killed by the reuse response.
        AddActiveToken(db, user.Id);
        db.Context.SaveChanges();
        var originalStamp = user.SecurityStamp;

        var handler = new RefreshTokenCommandHandler(db.NewContext(), _jwt, _clock);
        var act = () => handler.Handle(
            new RefreshTokenCommand { RefreshToken = reused.RawToken }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();

        using var read = db.NewContext();
        (await read.RefreshTokens.CountAsync(t => t.RevokedAt == null)).Should().Be(0);
        (await read.Users.SingleAsync()).SecurityStamp.Should().NotBe(originalStamp);
    }

    [Fact]
    public async Task RevokeAll_RotatesStampAndRevokesEveryToken()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        AddActiveToken(db, user.Id);
        AddActiveToken(db, user.Id);
        var originalStamp = user.SecurityStamp;

        var current = new FakeCurrentUserService { UserId = user.Id, Role = "User" };
        var handler = new RevokeAllTokensCommandHandler(db.NewContext(), current, _clock);

        await handler.Handle(new RevokeAllTokensCommand(), CancellationToken.None);

        using var read = db.NewContext();
        (await read.RefreshTokens.CountAsync(t => t.RevokedAt == null)).Should().Be(0);
        (await read.Users.SingleAsync()).SecurityStamp.Should().NotBe(originalStamp);
    }

    [Fact]
    public async Task RevokeAll_ForAnotherUser_AsNonAdmin_ThrowsForbidden()
    {
        using var db = new TestDatabase();
        var me = SeedUser(db);
        var other = new User("other@example.com", "hash", _clock.UtcNow);
        db.Context.Users.Add(other);
        db.Context.SaveChanges();

        var current = new FakeCurrentUserService { UserId = me.Id, Role = "User" };
        var handler = new RevokeAllTokensCommandHandler(db.NewContext(), current, _clock);
        var act = () => handler.Handle(
            new RevokeAllTokensCommand { UserId = other.Id }, CancellationToken.None);

        await act.Should().ThrowAsync<ForbiddenAccessException>();
    }
}
