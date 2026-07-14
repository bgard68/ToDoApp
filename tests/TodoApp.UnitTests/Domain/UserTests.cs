using FluentAssertions;
using TodoApp.Domain.Entities;
using Xunit;

namespace TodoApp.UnitTests.Domain;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Constructor_NormalizesEmailAndStampsCreatedAt()
    {
        var user = new User("Test@Example.COM", "hash", Now);

        user.Email.Should().Be("test@example.com");
        user.HasPassword.Should().BeTrue();
        user.IsActive.Should().BeTrue();
        user.SecurityStamp.Should().NotBeNullOrEmpty();
        user.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void CreateExternal_HasNoPasswordButIsUsable()
    {
        var user = User.CreateExternal("ext@example.com", Now);

        user.PasswordHash.Should().BeNull();
        user.HasPassword.Should().BeFalse();
        user.IsActive.Should().BeTrue();
        user.SecurityStamp.Should().NotBeNullOrEmpty();
        user.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void RotateSecurityStamp_ChangesTheStamp()
    {
        var user = new User("a@b.com", "hash", Now);
        var original = user.SecurityStamp;

        user.RotateSecurityStamp(Now.AddMinutes(1));

        user.SecurityStamp.Should().NotBe(original);
        user.UpdatedAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Deactivate_DisablesAndRotatesStamp()
    {
        var user = new User("a@b.com", "hash", Now);
        var original = user.SecurityStamp;

        user.Deactivate(Now.AddMinutes(1));

        user.IsActive.Should().BeFalse();
        user.SecurityStamp.Should().NotBe(original);
    }
}
