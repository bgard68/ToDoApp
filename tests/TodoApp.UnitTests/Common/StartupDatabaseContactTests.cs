using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TodoApp.Infrastructure.Authentication;
using TodoApp.Infrastructure.Persistence;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Common;

/// <summary>
/// The startup initializer exists so a serverless database's first-run setup happens once.
/// Every call it makes opens a connection, and on a scale-to-zero database that connection IS
/// the wake-up — billed as a full minimum interval. So "off" has to mean no contact at all,
/// not merely "skip the schema check": a seed probe left outside the gate costs exactly what
/// the schema check it replaced did. That regression shipped once; this is the guard.
/// </summary>
public class StartupDatabaseContactTests
{
    private static ApplicationDbContext UnreachableContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            // A path that cannot be opened: any database call at all throws.
            .UseSqlite("Data Source=/nonexistent-directory/unreachable.db")
            .Options);

    [Fact]
    public async Task InitializationOffMakesNoDatabaseCall_EvenWhenSeedingIsEnabled()
    {
        await using var context = UnreachableContext();

        // Seeding ENABLED is the case that regressed: the seed probe ran regardless of the flag.
        var seed = new DemoSeedOptions { DemoUser = true, Email = "demo@example.com", Password = "x" };

        var act = async () => await DbInitializer.InitializeAsync(
            context, new PasswordHasher(), new FakeDateTimeProvider(), seed, initializeOnStartup: false);

        await act.Should().NotThrowAsync(
            "initializeOnStartup:false must skip the seed probe too, not just EnsureCreated");
    }

    [Fact]
    public async Task InitializationOnDoesReachTheDatabase()
    {
        await using var context = UnreachableContext();

        var act = async () => await DbInitializer.InitializeAsync(
            context, new PasswordHasher(), new FakeDateTimeProvider(), seed: null, initializeOnStartup: true);

        // Proves the test's premise: this context genuinely fails on contact, so the assertion
        // above means "never called", not "called against something that happened to work".
        await act.Should().ThrowAsync<Exception>();
    }
}
