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

    private RegisterCommandHandler Register(TestDatabase db) =>
        new(db.Users, db.Categories, db.RefreshTokens, db.UnitOfWork, _hasher, _jwt, _clock);

    private LoginCommandHandler Login(TestDatabase db) =>
        new(db.Users, db.RefreshTokens, _hasher, _jwt, _clock);

    [Fact]
    public async Task Register_CreatesUserAndIssuesTokens()
    {
        using var db = new TestDatabase();

        var response = await Register(db).Handle(
            new RegisterCommand { Email = "New@Example.com", Password = "Password1" },
            CancellationToken.None);

        response.AccessToken.Should().NotBeNullOrEmpty();
        response.RefreshToken.Should().NotBeNullOrEmpty();
        response.User.Email.Should().Be("new@example.com");

        (await db.CountAsync("Users")).Should().Be(1);
        (await db.CountAsync("RefreshTokens")).Should().Be(1);
    }

    [Fact]
    public async Task Register_DuplicateEmail_ThrowsConflict()
    {
        using var db = new TestDatabase();
        await db.Users.AddAsync(new User("dupe@example.com", _hasher.Hash("Password1"), _clock.UtcNow), CancellationToken.None);

        var act = () => Register(db).Handle(
            new RegisterCommand { Email = "dupe@example.com", Password = "Password1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Login_WithValidCredentials_ReturnsTokens()
    {
        using var db = new TestDatabase();
        await db.Users.AddAsync(new User("user@example.com", _hasher.Hash("Password1"), _clock.UtcNow), CancellationToken.None);

        var response = await Login(db).Handle(
            new LoginCommand { Email = "user@example.com", Password = "Password1" },
            CancellationToken.None);

        response.AccessToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_WithWrongPassword_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        await db.Users.AddAsync(new User("user@example.com", _hasher.Hash("Password1"), _clock.UtcNow), CancellationToken.None);

        var act = () => Login(db).Handle(
            new LoginCommand { Email = "user@example.com", Password = "WrongPass1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Login_ExternalOnlyUser_ThrowsUnauthorized()
    {
        using var db = new TestDatabase();
        await db.Users.AddAsync(User.CreateExternal("google@example.com", _clock.UtcNow), CancellationToken.None);

        var act = () => Login(db).Handle(
            new LoginCommand { Email = "google@example.com", Password = "anything1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }
}
