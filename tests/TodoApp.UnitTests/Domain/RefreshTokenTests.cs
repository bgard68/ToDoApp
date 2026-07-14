using FluentAssertions;
using TodoApp.Domain.Entities;
using Xunit;

namespace TodoApp.UnitTests.Domain;

public class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewToken_IsActive()
    {
        var token = new RefreshToken(1, "hash", Now.AddDays(7), Now);

        token.IsActive(Now).Should().BeTrue();
        token.IsRevoked.Should().BeFalse();
        token.IsExpired(Now).Should().BeFalse();
    }

    [Fact]
    public void Revoke_MarksTokenRevokedAndInactive()
    {
        var token = new RefreshToken(1, "hash", Now.AddDays(7), Now);

        token.Revoke("Rotated", Now.AddMinutes(1), "next-hash");

        token.IsRevoked.Should().BeTrue();
        token.IsActive(Now.AddMinutes(1)).Should().BeFalse();
        token.RevokedReason.Should().Be("Rotated");
        token.ReplacedByTokenHash.Should().Be("next-hash");
    }

    [Fact]
    public void Token_IsExpired_AfterExpiryInstant()
    {
        var token = new RefreshToken(1, "hash", Now.AddDays(7), Now);

        token.IsExpired(Now.AddDays(7)).Should().BeTrue();   // expiry is inclusive
        token.IsActive(Now.AddDays(8)).Should().BeFalse();
    }
}
