using FluentAssertions;
using TodoApp.Infrastructure.Authentication;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// The hasher's rejection paths. A malformed stored hash must fail verification rather than
/// throw, so a corrupted row cannot turn a failed login into a 500.
/// </summary>
public class PasswordHasherEdgeCaseTests
{
    private readonly PasswordHasher _hasher = new();

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Hashing_RequiresAPassword(string? password)
    {
        var act = () => _hasher.Hash(password!);

        act.Should().Throw<ArgumentException>().WithParameterName("password");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void VerifyingAgainstABlankHash_IsFalse(string? hash)
    {
        _hasher.Verify(hash!, "Password1").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void VerifyingABlankPassword_IsFalse(string? password)
    {
        _hasher.Verify(_hasher.Hash("Password1"), password!).Should().BeFalse();
    }

    [Theory]
    [InlineData("not-a-hash")]                  // no separators
    [InlineData("600000.onlytwoparts")]         // too few parts
    [InlineData("notanumber.c2FsdA==.a2V5")]    // iteration count is not an integer
    [InlineData("0.c2FsdA==.a2V5")]             // zero iterations
    [InlineData("-1.c2FsdA==.a2V5")]            // negative iterations
    [InlineData("600000.not!base64.a2V5")]      // salt is not base64
    [InlineData("600000.c2FsdA==.not!base64")]  // key is not base64
    [InlineData("600000..")]                    // empty salt and key
    public void AMalformedHash_FailsVerificationWithoutThrowing(string hash)
    {
        _hasher.Verify(hash, "Password1").Should().BeFalse();
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("600000.not!base64.a2V5")]
    [InlineData("600000..")]
    public void AMalformedHash_IsFlaggedForRehash(string hash)
    {
        // Unparseable means the next successful sign-in should replace it.
        _hasher.NeedsRehash(hash).Should().BeTrue();
    }

    [Fact]
    public void TheWrongPasswordDoesNotVerify()
    {
        var hash = _hasher.Hash("Password1");

        _hasher.Verify(hash, "Password2").Should().BeFalse();
    }
}
