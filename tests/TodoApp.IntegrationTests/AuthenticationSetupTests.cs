using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TodoApp.WebApi.Authentication;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// The signing-key guard. A missing or too-short key must stop the app at startup rather than
/// let it run with authentication that cannot be trusted.
/// </summary>
public class AuthenticationSetupTests
{
    private static IServiceCollection Register(string? key)
    {
        var settings = new Dictionary<string, string?> { ["Jwt:Issuer"] = "TodoApp" };
        if (key is not null)
        {
            settings["Jwt:Key"] = key;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection().AddJwtAuthentication(configuration);
    }

    [Fact]
    public void NoJwtSectionAtAll_StopsStartup()
    {
        var configuration = new ConfigurationBuilder().Build();

        var act = () => new ServiceCollection().AddJwtAuthentication(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 bytes*");
    }

    [Theory]
    [InlineData(null)]          // the section exists but carries no key
    [InlineData("")]            // present but empty
    [InlineData("   ")]         // whitespace
    [InlineData("too-short")]   // under 256 bits
    [InlineData("31-bytes-is-still-one-too-few!!")]
    public void AWeakOrMissingKey_StopsStartup(string? key)
    {
        var act = () => Register(key);

        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 bytes*");
    }

    [Fact]
    public void AKeyOfExactlyTheMinimumLengthIsAccepted()
    {
        var key = new string('k', 32); // 32 ASCII characters == 32 bytes == 256 bits

        var act = () => Register(key);

        act.Should().NotThrow();
    }

    [Fact]
    public void AValidKeyRegistersAuthentication()
    {
        var services = Register("a-perfectly-adequate-signing-key-of-sufficient-length");

        services.Should().NotBeEmpty();
    }
}
