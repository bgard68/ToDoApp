using FluentAssertions;
using TodoApp.Application.Auth.Commands.GoogleSignIn;
using Xunit;

namespace TodoApp.UnitTests.Common;

public class ValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GoogleSignIn_RequiresAnIdToken(string idToken)
    {
        var result = new GoogleSignInCommandValidator()
            .Validate(new GoogleSignInCommand { IdToken = idToken });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be("A Google ID token is required.");
    }

    [Fact]
    public void GoogleSignIn_AcceptsANonEmptyToken()
    {
        var result = new GoogleSignInCommandValidator()
            .Validate(new GoogleSignInCommand { IdToken = "header.payload.signature" });

        result.IsValid.Should().BeTrue();
    }
}
