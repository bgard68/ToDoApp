using FluentAssertions;
using TodoApp.Application.Auth.Commands.Login;
using TodoApp.Application.Auth.Commands.Register;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Domain.Entities;
using TodoApp.Infrastructure.Authentication;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Auth;

public class RegisterLoginTests
{
    private readonly PasswordHasher _hasher = new();
    private readonly FakeJwtTokenService _jwt = new();
    private readonly FakeDateTimeProvider _clock = new();

    [Fact]
    public async Task Register_CreatesUserAndIssuesTokens()
    {
        using var db = new TestDatabase();
        var handler = new RegisterCommandHandler(db.Context, _hasher, _jwt, _clock);

        var response = await handler.Handle(
            new RegisterCommand { Email = "New@Example.com", Password = "Password1" },
            CancellationToken.None);

        response.AccessToken.Should().NotBeNullOrEmpty();
        response.RefreshToken.Should().NotBeNullOrEmpty();
        response.User.Email.Should().Be("new@example.com");

        using var read = db.NewContext();
        read.Users.Should().ContainSingle();
        read.RefreshTokens.Should().ContainSingle();
    }

    [Fact]
    public async Task Register_DuplicateEmail_ThrowsConflict()
    {
        using var db = new TestDatabase();
        db.Context.Users.Add(new User("dupe@example.com", _hasher.Hash("Password1"), _clock.UtcNow));
        db.Context.SaveChanges();

        var handler = new RegisterCommandHandler(db.NewContext(), _hasher, _jwt, _clock);
        var act = () => handler.Handle(
            new RegisterCommand { Email = "dupe@example.com", Password = "Password1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokens()
    {
        using var db = new TestDatabase();
        db.Context.Users.Add(new User("user@example.com", _hasher.Hash("Password1"), _clock.UtcNow));
        db.Context.SaveChanges();

        var handler = new LoginCommandHandler(db.NewContext(), _hasher, _jwt, _clock);
        var response = await handler.Handle(
            new LoginCommand { Email = "user@example.com", Password = "Password1" },
            CancellationToken.None);

        response.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithWrongPassword_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        db.Context.Users.Add(new User("user@example.com", _hasher.Hash("Password1"), _clock.UtcNow));
        db.Context.SaveChanges();

        var handler = new LoginCommandHandler(db.NewContext(), _hasher, _jwt, _clock);
        var act = () => handler.Handle(
            new LoginCommand { Email = "user@example.com", Password = "WrongPass1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Login_ExternalOnlyUser_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        db.Context.Users.Add(User.CreateExternal("google@example.com", _clock.UtcNow));
        db.Context.SaveChanges();

        var handler = new LoginCommandHandler(db.NewContext(), _hasher, _jwt, _clock);
        var act = () => handler.Handle(
            new LoginCommand { Email = "google@example.com", Password = "anything1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }
}
