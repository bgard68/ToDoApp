using FluentAssertions;
using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using TodoApp.Infrastructure.Authentication;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// The offline half of Google token validation: the configuration guard, the rejection of a
/// token that never parses, and the payload mapping. Verifying a real signature needs Google's
/// live signing keys, so that step is not exercised here.
/// </summary>
public class GoogleTokenValidatorTests
{
    private static GoogleTokenValidator Create(string? clientId) =>
        new(Options.Create(new GoogleAuthSettings { ClientId = clientId! }));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task WithoutAConfiguredClientId_FailsLoudly(string? clientId)
    {
        var act = () => Create(clientId).ValidateAsync("any-token", CancellationToken.None);

        // A misconfigured deployment must not silently reject every Google sign-in as invalid.
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Authentication:Google:ClientId*");
    }

    // An empty token is not covered here: it never reaches this class, because
    // GoogleSignInCommandValidator rejects it before the handler runs.
    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    public async Task AnUnparseableToken_IsRejectedAsNull(string idToken)
    {
        var result = await Create("client-id.apps.googleusercontent.com")
            .ValidateAsync(idToken, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public void FromPayload_CarriesTheIdentityWeActuallyUse()
    {
        var info = GoogleTokenValidator.FromPayload(new GoogleJsonWebSignature.Payload
        {
            Subject = "sub-123",
            Email = "person@example.com",
            EmailVerified = true,
            Name = "A Person"
        });

        info.Subject.Should().Be("sub-123");
        info.Email.Should().Be("person@example.com");
        info.EmailVerified.Should().BeTrue();
        info.Name.Should().Be("A Person");
    }

    [Fact]
    public void FromPayload_KeepsAnUnverifiedEmailFlagged()
    {
        var info = GoogleTokenValidator.FromPayload(new GoogleJsonWebSignature.Payload
        {
            Subject = "sub-456",
            Email = "unverified@example.com",
            EmailVerified = false,
            Name = null
        });

        info.EmailVerified.Should().BeFalse();
        info.Name.Should().BeNull();
    }
}
