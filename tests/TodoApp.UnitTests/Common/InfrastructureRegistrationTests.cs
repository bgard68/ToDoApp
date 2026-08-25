using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Infrastructure;
using TodoApp.Infrastructure.Persistence;
using Xunit;

namespace TodoApp.UnitTests.Common;

/// <summary>
/// The composition root picks the database provider from configuration, so the same build runs
/// on SQLite locally and Azure SQL in production. Getting that wrong is a deploy-time failure,
/// which is exactly when it is most expensive to find.
/// </summary>
public class InfrastructureRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddInfrastructure(configuration)
            .BuildServiceProvider();
    }

    [Fact]
    public void DefaultsToSqlite()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        context.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.Sqlite");
    }

    [Fact]
    public void UsesTheConfiguredSqliteConnectionString()
    {
        using var provider = Build(("ConnectionStrings:DefaultConnection", "Data Source=custom.db"));
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        context.Database.GetConnectionString().Should().Be("Data Source=custom.db");
    }

    [Fact]
    public void SqlServerProviderIsSelectedByConfiguration()
    {
        using var provider = Build(
            ("Database:Provider", "SqlServer"),
            ("ConnectionStrings:DefaultConnection", "Server=localhost;Database=Todo;Integrated Security=true;"));
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Registration only — nothing here opens a connection.
        context.Database.ProviderName.Should().Be("Microsoft.EntityFrameworkCore.SqlServer");
    }

    [Fact]
    public void SqlServerWithoutAConnectionString_FailsLoudly()
    {
        using var provider = Build(("Database:Provider", "SqlServer"));
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ConnectionStrings:DefaultConnection*");
    }

    [Fact]
    public void RegistersTheApplicationLayerAbstractions()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<IApplicationDbContext>().Should().BeOfType<ApplicationDbContext>();
        services.GetRequiredService<IDateTimeProvider>().Should().NotBeNull();
        services.GetRequiredService<ICurrentUserService>().Should().NotBeNull();
        services.GetRequiredService<IPasswordHasher>().Should().NotBeNull();
        services.GetRequiredService<IJwtTokenService>().Should().NotBeNull();
        services.GetRequiredService<IGoogleTokenValidator>().Should().NotBeNull();
        services.GetRequiredService<IBreachedPasswordChecker>().Should().NotBeNull();
    }
}
