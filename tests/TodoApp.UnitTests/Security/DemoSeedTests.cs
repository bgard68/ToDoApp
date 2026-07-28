using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TodoApp.Application.Common.Interfaces;
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

    /// <summary>
    /// DbInitializer resolves its collaborators from a service provider, so hand it one backed by
    /// the Dapper test harness — real SQL, real repositories, same path as production startup.
    /// </summary>
    private static ServiceProvider ProviderFor(TestDatabase db) =>
        new ServiceCollection()
            .AddSingleton<ISchemaInitializer>(new NoopSchemaInitializer())
            .AddSingleton(db.Users)
            .AddSingleton(db.Categories)
            .AddSingleton(db.Todos)
            .AddSingleton(db.UnitOfWork)
            .AddSingleton<IPasswordHasher>(Hasher)
            .AddSingleton<IDateTimeProvider>(new FakeDateTimeProvider())
            .BuildServiceProvider();

    [Fact]
    public async Task Does_not_seed_a_demo_user_by_default()
    {
        using var db = new TestDatabase();
        await using var services = ProviderFor(db);

        await DbInitializer.InitializeAsync(services);

        (await db.CountAsync("Users")).Should()
            .Be(0, "seeding is opt-in — a fresh production database must have no accounts");
    }

    [Fact]
    public async Task Does_not_seed_when_options_are_supplied_but_disabled()
    {
        using var db = new TestDatabase();
        await using var services = ProviderFor(db);

        await DbInitializer.InitializeAsync(services,
            new DemoSeedOptions { DemoUser = false, Password = "irrelevant" });

        (await db.CountAsync("Users")).Should().Be(0);
    }

    [Fact]
    public async Task Seeds_the_demo_user_when_explicitly_enabled()
    {
        using var db = new TestDatabase();
        await using var services = ProviderFor(db);

        await DbInitializer.InitializeAsync(services,
            new DemoSeedOptions { DemoUser = true, Email = "demo@example.test", Password = "Sup3rSecret!" });

        var user = await db.Users.GetByEmailAsync("demo@example.test", default);
        user.Should().NotBeNull();
        Hasher.Verify(user!.PasswordHash!, "Sup3rSecret!").Should().BeTrue();

        (await db.CountAsync("TodoItems")).Should().BeGreaterThan(0, "the sample board should be populated");
    }

    [Fact]
    public async Task Enabled_seed_without_a_configured_password_is_not_signable_in()
    {
        using var db = new TestDatabase();
        await using var services = ProviderFor(db);

        await DbInitializer.InitializeAsync(services,
            new DemoSeedOptions { DemoUser = true, Email = "demo@todoapp.local", Password = "" });

        var user = await db.Users.GetByEmailAsync("demo@todoapp.local", default);
        user.Should().NotBeNull();

        // Fails closed: a random password, not a fallback constant. The old hard-coded
        // "Password123!" must not work.
        Hasher.Verify(user!.PasswordHash!, "Password123!").Should().BeFalse();
        Hasher.Verify(user.PasswordHash!, string.Empty).Should().BeFalse();
    }

    [Fact]
    public async Task Is_idempotent_when_users_already_exist()
    {
        using var db = new TestDatabase();
        await using var services = ProviderFor(db);
        var options = new DemoSeedOptions { DemoUser = true, Password = "Sup3rSecret!" };

        await DbInitializer.InitializeAsync(services, options);
        await DbInitializer.InitializeAsync(services, options);

        (await db.CountAsync("Users")).Should().Be(1);
    }

    /// <summary>The harness already built the schema; don't run the DDL twice.</summary>
    private sealed class NoopSchemaInitializer : ISchemaInitializer
    {
        public Task EnsureCreatedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
