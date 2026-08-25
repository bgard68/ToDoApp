using FluentAssertions;
using TodoApp.Domain.Entities;
using Xunit;

namespace TodoApp.UnitTests.Domain;

/// <summary>
/// The entity invariants: every constructor and mutator rejects the states the rest of the
/// system assumes cannot exist.
/// </summary>
public class EntityGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- Category ------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Category_RequiresAnOwner(int userId)
    {
        var act = () => new Category(userId, "Work", "#fff", Now);

        act.Should().Throw<ArgumentException>().WithParameterName("userId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Category_RequiresAName(string name)
    {
        var act = () => new Category(1, name, "#fff", Now);

        act.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Category_RequiresAColor(string color)
    {
        var act = () => new Category(1, "Work", color, Now);

        act.Should().Throw<ArgumentException>().WithParameterName("color");
    }

    [Fact]
    public void Category_TrimsNameAndColor()
    {
        var category = new Category(1, "  Work  ", "  #fff  ", Now);

        category.Name.Should().Be("Work");
        category.Color.Should().Be("#fff");
        category.UserId.Should().Be(1);
        category.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void Category_Update_ReplacesNameAndColorAndStampsUpdatedAt()
    {
        var category = new Category(1, "Work", "#fff", Now);
        var later = Now.AddHours(1);

        category.Update("  Studies  ", "  #000  ", later);

        category.Name.Should().Be("Studies");
        category.Color.Should().Be("#000");
        category.UpdatedAt.Should().Be(later);
    }

    [Fact]
    public void Category_Update_StillRejectsABlankName()
    {
        var category = new Category(1, "Work", "#fff", Now);

        var act = () => category.Update("  ", "#000", Now);

        act.Should().Throw<ArgumentException>().WithParameterName("name");
    }

    [Fact]
    public void Category_Update_StillRejectsABlankColor()
    {
        var category = new Category(1, "Work", "#fff", Now);

        var act = () => category.Update("Studies", "  ", Now);

        act.Should().Throw<ArgumentException>().WithParameterName("color");
    }

    [Fact]
    public void Category_DefaultsFor_SeedsTheStarterSet()
    {
        var defaults = Category.DefaultsFor(7, Now).ToList();

        defaults.Should().HaveCount(5);
        defaults.Should().OnlyContain(c => c.UserId == 7 && c.CreatedAt == Now);
        defaults.Select(c => c.Name).Should()
            .BeEquivalentTo(["Work", "Personal", "Errands", "Study", "Other"]);
    }

    // ---- ExternalLogin -------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ExternalLogin_RequiresAUser(int userId)
    {
        var act = () => new ExternalLogin(userId, "Google", "sub", Now);

        act.Should().Throw<ArgumentException>().WithParameterName("userId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExternalLogin_RequiresAProvider(string provider)
    {
        var act = () => new ExternalLogin(1, provider, "sub", Now);

        act.Should().Throw<ArgumentException>().WithParameterName("provider");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ExternalLogin_RequiresAProviderKey(string providerKey)
    {
        var act = () => new ExternalLogin(1, "Google", providerKey, Now);

        act.Should().Throw<ArgumentException>().WithParameterName("providerKey");
    }

    [Fact]
    public void ExternalLogin_StoresTheProviderIdentity()
    {
        var login = new ExternalLogin(3, "Google", "sub-9", Now);

        login.UserId.Should().Be(3);
        login.Provider.Should().Be("Google");
        login.ProviderKey.Should().Be("sub-9");
        login.CreatedAt.Should().Be(Now);
    }

    // ---- RefreshToken --------------------------------------------------------------

    [Fact]
    public void RefreshToken_RequiresAHash()
    {
        var act = () => new RefreshToken(1, null!, Now.AddDays(1), Now);

        act.Should().Throw<ArgumentNullException>().WithParameterName("tokenHash");
    }

    [Fact]
    public void RefreshToken_Revoke_IsIdempotentAndKeepsTheFirstReason()
    {
        var token = new RefreshToken(1, "hash", Now.AddDays(1), Now);

        token.Revoke("Rotated", Now, "next-hash");
        token.Revoke("Logout", Now.AddHours(1), "other-hash");

        token.RevokedReason.Should().Be("Rotated");
        token.RevokedAt.Should().Be(Now);
        token.ReplacedByTokenHash.Should().Be("next-hash");
    }

    // ---- User ----------------------------------------------------------------------

    [Fact]
    public void User_RequiresAPasswordHash()
    {
        var act = () => new User("a@b.com", null!, Now);

        act.Should().Throw<ArgumentNullException>().WithParameterName("passwordHash");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void User_NormalizeEmail_RejectsABlankAddress(string? email)
    {
        var act = () => User.NormalizeEmail(email!);

        act.Should().Throw<ArgumentException>().WithParameterName("email");
    }

    [Fact]
    public void User_NormalizeEmail_TrimsAndLowercases()
    {
        User.NormalizeEmail("  Mixed.Case@Example.COM  ").Should().Be("mixed.case@example.com");
    }

    [Fact]
    public void User_SetPassword_RotatesTheSecurityStamp()
    {
        var user = new User("a@b.com", "old", Now);
        var stamp = user.SecurityStamp;

        user.SetPassword("new", Now.AddMinutes(5));

        user.PasswordHash.Should().Be("new");
        user.SecurityStamp.Should().NotBe(stamp); // every outstanding access token dies
        user.UpdatedAt.Should().Be(Now.AddMinutes(5));
    }

    [Fact]
    public void User_SetPassword_RejectsNull()
    {
        var user = new User("a@b.com", "old", Now);

        var act = () => user.SetPassword(null!, Now);

        act.Should().Throw<ArgumentNullException>().WithParameterName("passwordHash");
    }

    [Fact]
    public void User_UpgradePasswordHash_RejectsNull()
    {
        var user = new User("a@b.com", "old", Now);

        var act = () => user.UpgradePasswordHash(null!, Now);

        act.Should().Throw<ArgumentNullException>().WithParameterName("passwordHash");
    }

    [Fact]
    public void User_Activate_RestoresAccessWithoutRotatingTheStamp()
    {
        var user = new User("a@b.com", "hash", Now);
        user.Deactivate(Now.AddMinutes(1));
        var stampWhileDisabled = user.SecurityStamp;

        user.Activate(Now.AddMinutes(2));

        user.IsActive.Should().BeTrue();
        user.SecurityStamp.Should().Be(stampWhileDisabled);
        user.UpdatedAt.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void User_CreateExternal_HasNoLocalPassword()
    {
        var user = User.CreateExternal("  Ext@Example.com ", Now);

        user.Email.Should().Be("ext@example.com");
        user.PasswordHash.Should().BeNull();
        user.HasPassword.Should().BeFalse();
        user.IsActive.Should().BeTrue();
        user.CreatedAt.Should().Be(Now);
    }
}
