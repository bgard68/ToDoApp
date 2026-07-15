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
        // Provider is chosen by config so the same build runs on SQLite locally and Azure SQL
        // (SQL Server) in production — set Database:Provider=SqlServer + a connection string.
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

        return services;
    }
}
