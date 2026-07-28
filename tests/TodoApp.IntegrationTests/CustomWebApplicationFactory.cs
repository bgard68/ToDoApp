using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TodoApp.Infrastructure.Persistence;

namespace TodoApp.IntegrationTests;

/// <summary>
/// Hosts the real API in-process for integration tests. Each factory instance owns a
/// private in-memory SQLite database (kept alive by a single open connection for the
/// factory's lifetime). The DbContext is swapped via ConfigureTestServices, and the
/// Development environment supplies a valid JWT signing key from appsettings.Development.json.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    // A throwaway key used only by the test host. Supplied via an environment variable so
    // no secret has to live in appsettings. The value is constant, so setting it from every
    // factory instance is race-free even when test classes run in parallel.
    private const string TestSigningKey =
        "integration-test-signing-key-that-is-definitely-long-enough-123456";

    private readonly SqliteConnection _connection;

    public CustomWebApplicationFactory()
    {
        Environment.SetEnvironmentVariable("Jwt__Key", TestSigningKey);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
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
        foreach (var (key, value) in TestConfiguration)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            // Replace the real (file-based) DbContext with our shared in-memory connection.
            var toRemove = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>)
                         || d.ServiceType == typeof(ApplicationDbContext))
                .ToList();

            foreach (var descriptor in toRemove)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection.Dispose();
        }

        base.Dispose(disposing);
    }
}
