using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Infrastructure.Authentication;
using TodoApp.Infrastructure.Persistence;
using TodoApp.Infrastructure.Time;
using TodoApp.WebApi;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// Startup must survive a database that is not ready yet — an Azure SQL serverless instance
/// waking from auto-pause is the case this exists for. A failure here has to defer, not kill the
/// app, and the retry has to actually recover once the database comes back.
/// </summary>
public class DatabaseStartupTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DatabaseSwitch _database = new();

    public DatabaseStartupTests() => _connection.Open();

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_database);
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();
        services.AddScoped(_ =>
        {
            if (!_database.IsReachable)
            {
                throw new InvalidOperationException("the database is still waking up");
            }

            return new ApplicationDbContext(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        });

        return services.BuildServiceProvider();
    }

    private bool SchemaExists()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Users';";
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    [Fact]
    public async Task AReachableDatabaseIsInitializedInlineWithNoBackgroundWork()
    {
        using var services = BuildServices();

        var background = await DatabaseStartup.InitializeAsync(
            services, new DemoSeedOptions(), NullLogger.Instance,
            retryDelay: TimeSpan.FromMilliseconds(10), maxRetryAttempts: 1);

        background.Should().BeNull(); // nothing was deferred
        SchemaExists().Should().BeTrue();
    }

    [Fact]
    public async Task TheDemoSeedIsAppliedWhenExplicitlyEnabled()
    {
        using var services = BuildServices();

        await DatabaseStartup.InitializeAsync(
            services,
            new DemoSeedOptions { DemoUser = true, Email = "demo@todoapp.local", Password = "Password123!" },
            NullLogger.Instance,
            retryDelay: TimeSpan.FromMilliseconds(10),
            maxRetryAttempts: 1);

        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await context.Users.AnyAsync(u => u.Email == "demo@todoapp.local")).Should().BeTrue();
    }

    [Fact]
    public async Task AnUnreachableDatabaseDefersInsteadOfFailingStartup()
    {
        using var services = BuildServices();
        _database.IsReachable = false;

        var background = await DatabaseStartup.InitializeAsync(
            services, new DemoSeedOptions(), NullLogger.Instance,
            retryDelay: TimeSpan.FromMilliseconds(10),
            maxRetryAttempts: 10);

        // Startup got past it: the caller holds a task, not an exception.
        background.Should().NotBeNull();

        _database.IsReachable = true;
        await background!;

        SchemaExists().Should().BeTrue();
    }

    [Fact]
    public async Task TheBackgroundRetryGivesUpQuietlyWhenTheDatabaseNeverComesBack()
    {
        using var services = BuildServices();
        _database.IsReachable = false;

        var background = await DatabaseStartup.InitializeAsync(
            services, new DemoSeedOptions(), NullLogger.Instance,
            retryDelay: TimeSpan.FromMilliseconds(1),
            maxRetryAttempts: 2);

        // Exhausting the attempts must not throw on a background thread — that would take the
        // process down rather than leave a logged, still-serving app.
        var act = async () => await background!;

        await act.Should().NotThrowAsync();
        SchemaExists().Should().BeFalse();
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Stands in for a database that is paused and later wakes up.</summary>
    private sealed class DatabaseSwitch
    {
        public bool IsReachable { get; set; } = true;
    }
}
