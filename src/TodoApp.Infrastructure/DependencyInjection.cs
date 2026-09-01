using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Infrastructure.Authentication;
using TodoApp.Infrastructure.Persistence;
using TodoApp.Infrastructure.Time;

namespace TodoApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Provider is chosen by config so the same build runs on SQLite locally and a managed
        // Postgres (Neon) or Azure SQL in production — set Database:Provider plus a connection
        // string. Postgres is the deployed default: its compute bills by the minute rather than
        // charging a full hour every time a paused instance wakes.
        var provider = configuration.GetValue<string>("Database:Provider") ?? "Sqlite";

        services.AddDbContext<ApplicationDbContext>(options =>
        {
            if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                var sqlConnection = configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException(
                        "ConnectionStrings:DefaultConnection is required when Database:Provider is SqlServer.");
                // Retry transient failures — notably Azure SQL serverless "waking from
                // auto-pause" connection timeouts (error -2), so the first request after the
                // database has been idle succeeds instead of throwing a 500.
                options.UseSqlServer(sqlConnection, sql => sql.EnableRetryOnFailure(
                    maxRetryCount: 8,
                    maxRetryDelay: TimeSpan.FromSeconds(15),
                    errorNumbersToAdd: new[] { -2 }));
            }
            else if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase)
                  || provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
            {
                var npgConnection = configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException(
                        "ConnectionStrings:DefaultConnection is required when Database:Provider is Postgres.");
                // Retry transient failures the same way the SQL Server path does. Neon suspends an
                // idle compute after five minutes, so the first connection after a quiet spell can
                // fail while it resumes; Npgsql's execution strategy already classifies those
                // connection errors as transient, so no extra error codes are needed here.
                options.UseNpgsql(npgConnection, npgsql => npgsql.EnableRetryOnFailure(
                    maxRetryCount: 8,
                    maxRetryDelay: TimeSpan.FromSeconds(15),
                    errorCodesToAdd: null));
            }
            else
            {
                var sqliteConnection = configuration.GetConnectionString("DefaultConnection")
                    ?? "Data Source=todoapp.db";
                options.UseSqlite(sqliteConnection);
            }
        });

        services.AddScoped<IApplicationDbContext>(provider =>
            provider.GetRequiredService<ApplicationDbContext>());

        // System clock abstraction (injected wherever timestamps are needed).
        services.AddSingleton<IDateTimeProvider, DateTimeProvider>();

        // Auth / identity services.
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserService, CurrentUserService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();
        services.AddSingleton<IJwtTokenService, JwtTokenService>();

        // Google sign-in.
        services.Configure<GoogleAuthSettings>(configuration.GetSection(GoogleAuthSettings.SectionName));
        services.AddSingleton<IGoogleTokenValidator, GoogleTokenValidator>();

        // Breached-password rejection (review finding L9). Uses the free Have I Been Pwned
        // k-anonymity range API — no API key, so it costs nothing on the Free tier. The password
        // itself is never sent; only the first five characters of its SHA-1 hash.
        services.Configure<PasswordBreachCheckOptions>(
            configuration.GetSection(PasswordBreachCheckOptions.SectionName));
        services.AddHttpClient(HibpBreachedPasswordChecker.HttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
            client.Timeout = TimeSpan.FromSeconds(10);
            // HIBP asks callers to identify themselves.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TodoApp-SecurityCheck/1.0");
            // Pads responses with fake hashes so response size can't hint at the prefix's contents.
            client.DefaultRequestHeaders.Add("Add-Padding", "true");
        });
        services.AddScoped<IBreachedPasswordChecker, HibpBreachedPasswordChecker>();

        return services;
    }
}
