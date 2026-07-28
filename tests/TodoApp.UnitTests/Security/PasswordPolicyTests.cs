using FluentAssertions;
using TodoApp.Application.Auth.Commands;
using TodoApp.Application.Auth.Commands.Login;
using TodoApp.Application.Auth.Commands.Register;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// Review finding H3 — an unbounded password is attacker-controlled PBKDF2 cost. Both the login
/// and register paths must reject oversized input before it reaches the hasher.
/// </summary>
public class PasswordPolicyTests
{
    private static string OfLength(int n) => new('a', n);

    [Fact]
    public void Register_rejects_a_password_over_the_maximum_length()
    {
        var validator = new RegisterCommandValidator();
        var command = new RegisterCommand
        {
            Email = "user@example.test",
            Password = OfLength(PasswordPolicy.MaxLength + 1) + "1"
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(RegisterCommand.Password));
    }

    [Fact]
    public void Login_rejects_a_password_over_the_maximum_length()
    {
        var validator = new LoginCommandValidator();
        var command = new LoginCommand
        {
            Email = "user@example.test",
            Password = OfLength(PasswordPolicy.MaxLength + 1)
        };

        var result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(LoginCommand.Password));
    }

    [Fact]
    public void Login_rejects_a_multi_megabyte_password_without_hashing_it()
    {
        var validator = new LoginCommandValidator();
        var command = new LoginCommand
        {
            Email = "user@example.test",
            Password = OfLength(4 * 1024 * 1024)
        };

        validator.Validate(command).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_password_at_exactly_the_maximum_length_is_accepted()
    {
        var validator = new RegisterCommandValidator();
        var command = new RegisterCommand
        {
            Email = "user@example.test",
            Password = OfLength(PasswordPolicy.MaxLength - 1) + "1"
        };

        validator.Validate(command).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Register_still_enforces_the_minimum_length_and_composition()
    {
        var validator = new RegisterCommandValidator();

        validator.Validate(new RegisterCommand { Email = "user@example.test", Password = "ab1" })
            .IsValid.Should().BeFalse("shorter than the minimum");

        validator.Validate(new RegisterCommand { Email = "user@example.test", Password = "abcdefghij" })
            .IsValid.Should().BeFalse("no digit");
    }
}
