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
    private readonly FakeBreachedPasswordChecker _breachChecker = new();

    private RegisterCommandHandler Register(TestDatabase db) =>
        new(db.Users, db.Categories, db.RefreshTokens, db.UnitOfWork, _hasher, _jwt, _clock, _breachChecker);

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
    [Fact]
    public async Task Login_upgrades_a_legacy_hash_without_signing_the_user_out()
    {
        // Review finding L8: a hash created at the old 100k work factor must verify, then be
        // silently replaced with a 600k one — and the security stamp must NOT rotate, or the
        // user is signed out of every device at the moment they sign in.
        using var db = new TestDatabase();

        var salt = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "Password1", salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var legacyHash = string.Join('.', 100_000, Convert.ToBase64String(salt), Convert.ToBase64String(key));

        var user = new TodoApp.Domain.Entities.User("legacy@example.com", legacyHash, _clock.UtcNow);
        await db.Users.AddAsync(user, CancellationToken.None);
        var stampBefore = user.SecurityStamp;

        var response = await Login(db).Handle(
            new LoginCommand { Email = "legacy@example.com", Password = "Password1" },
            CancellationToken.None);

        response.AccessToken.Should().NotBeNullOrEmpty();

        var stored = await db.Users.GetByEmailAsync("legacy@example.com", CancellationToken.None);
        int.Parse(stored!.PasswordHash!.Split('.')[0]).Should().Be(600_000, "the hash should be upgraded in place");
        _hasher.Verify(stored.PasswordHash!, "Password1").Should().BeTrue("the same password still works");
        stored.SecurityStamp.Should().Be(stampBefore, "a rehash is not a credential change - sessions must survive");
    }
}
