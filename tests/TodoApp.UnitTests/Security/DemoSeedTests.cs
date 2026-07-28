using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TodoApp.Infrastructure.Authentication;
using TodoApp.Infrastructure.Persistence;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// Review finding H1 — the demo account must never appear unless it was explicitly asked for,
/// and its password must never come from a constant in the assembly.
/// </summary>
public class DemoSeedTests
{
    private static readonly PasswordHasher Hasher = new();
    private static FakeDateTimeProvider Clock() => new();

    [Fact]
    public async Task Does_not_seed_a_demo_user_by_default()
    {
        using var db = new TestDatabase();

        await DbInitializer.InitializeAsync(db.Context, Hasher, Clock());

        var users = await db.NewContext().Users.ToListAsync();
        users.Should().BeEmpty("seeding is opt-in — a fresh production database must have no accounts");
    }

    [Fact]
    public async Task Does_not_seed_when_options_are_supplied_but_disabled()
    {
        using var db = new TestDatabase();

        await DbInitializer.InitializeAsync(db.Context, Hasher, Clock(),
            new DemoSeedOptions { DemoUser = false, Password = "irrelevant" });

        (await db.NewContext().Users.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Seeds_the_demo_user_when_explicitly_enabled()
    {
        using var db = new TestDatabase();

        await DbInitializer.InitializeAsync(db.Context, Hasher, Clock(),
            new DemoSeedOptions { DemoUser = true, Email = "demo@example.test", Password = "Sup3rSecret!" });

        var fresh = db.NewContext();
        var user = await fresh.Users.SingleAsync();
        user.Email.Should().Be("demo@example.test");
        Hasher.Verify(user.PasswordHash!, "Sup3rSecret!").Should().BeTrue();

        (await fresh.TodoItems.CountAsync()).Should().BeGreaterThan(0, "the sample board should be populated");
    }

    [Fact]
    public async Task Enabled_seed_without_a_configured_password_is_not_signable_in()
    {
        using var db = new TestDatabase();

        await DbInitializer.InitializeAsync(db.Context, Hasher, Clock(),
            new DemoSeedOptions { DemoUser = true, Password = "" });

        var user = await db.NewContext().Users.SingleAsync();

        // Fails closed: a random password, not a fallback constant. The old hard-coded
        // "Password123!" must not work.
        Hasher.Verify(user.PasswordHash!, "Password123!").Should().BeFalse();
        Hasher.Verify(user.PasswordHash!, string.Empty).Should().BeFalse();
    }

    [Fact]
    public async Task Is_idempotent_when_users_already_exist()
    {
        using var db = new TestDatabase();
        var options = new DemoSeedOptions { DemoUser = true, Password = "Sup3rSecret!" };

        await DbInitializer.InitializeAsync(db.Context, Hasher, Clock(), options);
        await DbInitializer.InitializeAsync(db.Context, Hasher, Clock(), options);

        (await db.NewContext().Users.CountAsync()).Should().Be(1);
    }
}
