using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
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
