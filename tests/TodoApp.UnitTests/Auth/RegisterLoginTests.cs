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

    [Fact]
    public async Task Register_CreatesUserAndIssuesTokens()
    {
        using var db = new TestDatabase();
        var handler = new RegisterCommandHandler(db.Context, _hasher, _jwt, _clock, _breachChecker);

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

        var handler = new RegisterCommandHandler(db.NewContext(), _hasher, _jwt, _clock, _breachChecker);
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
    [Fact]
    public async Task Login_upgrades_a_legacy_hash_without_signing_the_user_out()
    {
        // Review finding L8: a hash created at the old 100k work factor must verify, then be
        // silently replaced with a 600k one — and the security stamp must NOT rotate, or the
        // user is signed out of every device at the moment they sign in.
        using var db = new TestDatabase();
        var real = new TodoApp.Infrastructure.Authentication.PasswordHasher();

        var salt = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "Password1", salt, 100_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var legacyHash = string.Join('.', 100_000, Convert.ToBase64String(salt), Convert.ToBase64String(key));

        var user = new TodoApp.Domain.Entities.User("legacy@example.com", legacyHash, _clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();
        var stampBefore = user.SecurityStamp;

        var handler = new LoginCommandHandler(db.Context, real, _jwt, _clock);
        var response = await handler.Handle(
            new LoginCommand { Email = "legacy@example.com", Password = "Password1" },
            CancellationToken.None);

        response.AccessToken.Should().NotBeNullOrEmpty();

        var stored = db.NewContext().Users.Single(u => u.Email == "legacy@example.com");
        int.Parse(stored.PasswordHash!.Split('.')[0]).Should().Be(600_000, "the hash should be upgraded in place");
        real.Verify(stored.PasswordHash!, "Password1").Should().BeTrue("the same password still works");
        stored.SecurityStamp.Should().Be(stampBefore, "a rehash is not a credential change - sessions must survive");
    }
}
