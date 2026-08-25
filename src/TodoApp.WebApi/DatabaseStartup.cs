using TodoApp.Application.Common.Interfaces;
using TodoApp.Infrastructure.Persistence;

namespace TodoApp.WebApi;

/// <summary>
/// Creates and seeds the database at startup, without letting a cold or paused database stop the
/// app from starting.
/// </summary>
/// <remarks>
/// Azure SQL serverless can be waking from auto-pause when the container comes up. Failing
/// startup there turns a slow database into a dead app, so an unreachable database is logged and
/// the initialization is retried off the startup path; requests in the meantime ride out the
/// wake-up via EF's <c>EnableRetryOnFailure</c>.
/// </remarks>
public static class DatabaseStartup
{
    /// <summary>
    /// Initializes the database, falling back to a background retry loop if the first attempt
    /// fails.
    /// </summary>
    /// <returns>
    /// <c>null</c> when the database was initialized immediately, otherwise the background retry
    /// task. Production discards it — startup must not wait on it — but a test can await it.
    /// </returns>
    public static async Task<Task?> InitializeAsync(
        IServiceProvider services,
        DemoSeedOptions demoSeed,
        ILogger logger,
        TimeSpan retryDelay,
        int maxRetryAttempts)
    {
        try
        {
            await InitializeOnceAsync(services, demoSeed);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Database initialization was deferred at startup (database may be resuming). " +
                "It will be retried in the background.");

            return Task.Run(() => RetryAsync(
                services, demoSeed, logger, retryDelay, maxRetryAttempts));
        }
    }

    private static async Task InitializeOnceAsync(IServiceProvider services, DemoSeedOptions demoSeed)
    {
        using var scope = services.CreateScope();
        var scoped = scope.ServiceProvider;

        await DbInitializer.InitializeAsync(
            scoped.GetRequiredService<ApplicationDbContext>(),
            scoped.GetRequiredService<IPasswordHasher>(),
            scoped.GetRequiredService<IDateTimeProvider>(),
            demoSeed);
    }

    private static async Task RetryAsync(
        IServiceProvider services,
        DemoSeedOptions demoSeed,
        ILogger logger,
        TimeSpan retryDelay,
        int maxRetryAttempts)
    {
        for (var attempt = 1; attempt <= maxRetryAttempts; attempt++)
        {
            await Task.Delay(retryDelay);

            try
            {
                await InitializeOnceAsync(services, demoSeed);
                logger.LogInformation(
                    "Database initialization completed on background attempt {Attempt}.", attempt);
                return;
            }
            catch (Exception retryEx)
            {
                logger.LogWarning(retryEx,
                    "Background database initialization attempt {Attempt} failed.", attempt);
            }
        }
    }
}
