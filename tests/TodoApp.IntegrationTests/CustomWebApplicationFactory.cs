using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace TodoApp.IntegrationTests;

/// <summary>
/// Hosts the real API in-process for integration tests. Each factory instance owns a private,
/// file-backed SQLite database in the temp directory; the app's own startup path builds the
/// schema and seeds demo data via SchemaInitializer/DbInitializer, exercising the real Dapper
/// wiring. A throwaway JWT signing key is supplied via environment variable.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    // A throwaway key used only by the test host. Supplied via an environment variable so no
    // secret has to live in appsettings. The value is constant, so setting it from every factory
    // instance is race-free even when test classes run in parallel.
    private const string TestSigningKey =
        "integration-test-signing-key-that-is-definitely-long-enough-123456";

    // A unique database file per factory instance keeps parallel test classes isolated.
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"todoapp-it-{Guid.NewGuid():N}.db");

    public CustomWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("Jwt__Key", TestSigningKey);
    }

    /// <summary>
    /// Configuration applied on top of appsettings for the test host. Rate limits are raised well
    /// clear of what the suite generates (the whole run shares one client-IP partition), and demo
    /// seeding is forced off so tests exercise the production default. Override to vary either.
    /// </summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> TestConfiguration =>
    [
        new("RateLimiting:Auth:PermitLimit", "10000"),
        new("RateLimiting:Global:PermitLimit", "10000"),
        new("Seed:DemoUser", "false"),
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting (rather than an extra configuration source) so these reliably win over
        // appsettings.Development.json in the minimal-hosting test host.
        foreach (var (key, value) in TestConfiguration)
        {
            builder.UseSetting(key, value);
        }

        // Point the real connection factory at this instance's private SQLite file.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:DefaultConnection"] = $"Data Source={_databasePath}"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            // Release pooled handles to the file before deleting it.
            SqliteConnection.ClearAllPools();
            try
            {
                File.Delete(_databasePath);
            }
            catch
            {
                // Best effort — the temp file is harmless if it lingers.
            }
        }
    }
}
