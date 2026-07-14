using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Auth.Commands.GoogleSignIn;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Models;
using TodoApp.Domain.Entities;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Auth;

public class GoogleSignInTests
{
    private readonly FakeJwtTokenService _jwt = new();
    private readonly FakeDateTimeProvider _clock = new();

    private GoogleSignInCommandHandler CreateHandler(TestDatabase db, GoogleUserInfo? payload) =>
        new(db.NewContext(), new FakeGoogleTokenValidator { Result = payload }, _jwt, _clock);

    [Fact]
    public async Task Google_NewEmail_CreatesUserAndExternalLogin()
    {
        using var db = new TestDatabase();
        var handler = CreateHandler(db, new GoogleUserInfo("sub-1", "new@example.com", true, "New User"));

        var response = await handler.Handle(new GoogleSignInCommand { IdToken = "token" }, CancellationToken.None);

        response.User.Email.Should().Be("new@example.com");
        response.AccessToken.Should().NotBeNullOrEmpty();

        using var read = db.NewContext();
        (await read.Users.CountAsync()).Should().Be(1);
        var login = await read.ExternalLogins.SingleAsync();
        login.Provider.Should().Be("Google");
        login.ProviderKey.Should().Be("sub-1");
    }

    [Fact]
    public async Task Google_ExistingEmail_LinksToExistingUserWithoutDuplicating()
    {
        using var db = new TestDatabase();
        db.Context.Users.Add(new User("existing@example.com", "hash", _clock.UtcNow));
        db.Context.SaveChanges();

        var handler = CreateHandler(db, new GoogleUserInfo("sub-2", "existing@example.com", true, "Existing"));
        await handler.Handle(new GoogleSignInCommand { IdToken = "token" }, CancellationToken.None);

        using var read = db.NewContext();
        (await read.Users.CountAsync()).Should().Be(1);
        (await read.ExternalLogins.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Google_InvalidToken_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        var handler = CreateHandler(db, payload: null);

        var act = () => handler.Handle(new GoogleSignInCommand { IdToken = "bad" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Google_UnverifiedEmail_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        var handler = CreateHandler(db, new GoogleUserInfo("sub-3", "unverified@example.com", false, null));

        var act = () => handler.Handle(new GoogleSignInCommand { IdToken = "token" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }
}
