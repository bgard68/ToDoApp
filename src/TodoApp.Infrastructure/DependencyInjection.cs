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

        return services;
    }
}
