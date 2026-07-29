using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Infrastructure.Authentication;
using TodoApp.Infrastructure.Persistence;
using TodoApp.Infrastructure.Persistence.Dapper;
using TodoApp.Infrastructure.Persistence.Repositories;
using TodoApp.Infrastructure.Time;

namespace TodoApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register Dapper's global type handlers (DateTimeOffset<->ticks, Guid<->text) once.
        DapperConfig.Register();

        // Data access. The connection factory (singleton) picks SQLite or SQL Server from
        // Database:Provider; the connection context + unit of work are per-scope so all
        // repositories in a request share one connection and can enlist in one transaction.
        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.AddScoped<IDbConnectionContext, DbConnectionContext>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<ISchemaInitializer, SchemaInitializer>();

        services.AddScoped<ITodoRepository, TodoRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IExternalLoginRepository, ExternalLoginRepository>();

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
