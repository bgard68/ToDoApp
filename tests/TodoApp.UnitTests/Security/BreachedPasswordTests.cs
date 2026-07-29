using FluentAssertions;
using TodoApp.Application.Auth.Commands.Register;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Infrastructure.Authentication;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// Review finding L9 — registration rejects passwords known to appear in breach corpora.
/// Composition rules alone stop nothing that matters: "Password1" satisfies every one of them.
/// </summary>
public class BreachedPasswordTests
{
    private readonly PasswordHasher _hasher = new();
    private readonly FakeJwtTokenService _jwt = new();
    private readonly FakeDateTimeProvider _clock = new();
    private readonly FakeBreachedPasswordChecker _breachChecker = new();

    private RegisterCommandHandler Register(TestDatabase db) =>
        new(db.Users, db.Categories, db.RefreshTokens, db.UnitOfWork, _hasher, _jwt, _clock, _breachChecker);

    [Fact]
    public async Task A_breached_password_is_rejected_with_a_validation_error()
    {
        using var db = new TestDatabase();
        _breachChecker.Breached = true;

        var act = () => Register(db).Handle(
            new RegisterCommand { Email = "someone@example.test", Password = "Password1" },
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().ContainKey(nameof(RegisterCommand.Password));
        ex.Which.Errors[nameof(RegisterCommand.Password)][0].Should().Contain("data breach");
    }

    [Fact]
    public async Task A_breached_password_does_not_create_an_account()
    {
        using var db = new TestDatabase();
        _breachChecker.Breached = true;

        try
        {
            await Register(db).Handle(
                new RegisterCommand { Email = "someone@example.test", Password = "Password1" },
                CancellationToken.None);
        }
        catch (ValidationException)
        {
            // expected
        }

        (await db.CountAsync("Users")).Should().Be(0, "the check runs before anything is persisted");
    }

    [Fact]
    public async Task A_clean_password_registers_normally()
    {
        using var db = new TestDatabase();
        _breachChecker.Breached = false;

        var response = await Register(db).Handle(
            new RegisterCommand { Email = "someone@example.test", Password = "Password1" },
            CancellationToken.None);

        response.AccessToken.Should().NotBeNullOrEmpty();
        _breachChecker.CallCount.Should().Be(1, "every registration must be checked");
    }

    [Fact]
    public async Task The_checker_failing_open_does_not_block_registration()
    {
        // The real implementation returns false on timeout / network error / non-200, so an
        // outage at the lookup service must never become an outage here.
        using var db = new TestDatabase();
        _breachChecker.Breached = false;

        var response = await Register(db).Handle(
            new RegisterCommand { Email = "someone@example.test", Password = "Password1" },
            CancellationToken.None);

        response.AccessToken.Should().NotBeNullOrEmpty();
    }
}
