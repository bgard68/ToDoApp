using System.Reflection;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;
using TodoApp.Infrastructure.Persistence.Converters;

namespace TodoApp.Infrastructure.Persistence;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<TodoItem> TodoItems => Set<TodoItem>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();

    public void SetOriginalConcurrencyToken(TodoItem entity, Guid token)
        => Entry(entity).Property(e => e.ConcurrencyToken).OriginalValue = token;

    /// <summary>
    /// Translates a provider-specific uniqueness error into
    /// <see cref="UniqueConstraintViolationException"/>, and leaves every other save failure
    /// alone.
    /// </summary>
    /// <remarks>
    /// Both public <c>SaveChangesAsync</c> overloads funnel through this one, as does
    /// <see cref="SaveChanges(bool)"/>, so the translation cannot be bypassed by calling a
    /// different overload. Nothing here swallows an exception: a failure that is not a
    /// uniqueness violation propagates untouched, and the one that is keeps the provider's
    /// error as its inner exception.
    /// </remarks>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueConstraintDetector.IsUniqueViolation(exception))
        {
            throw new UniqueConstraintViolationException(exception);
        }
    }

    /// <inheritdoc cref="SaveChangesAsync(bool, CancellationToken)"/>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateException exception) when (UniqueConstraintDetector.IsUniqueViolation(exception))
        {
            throw new UniqueConstraintViolationException(exception);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Store every DateTimeOffset as a UTC-tick long so SQLite can order and compare them.
    /// (No-op semantically on providers like SQL Server / PostgreSQL that support the type
    /// natively, but harmless and keeps behavior identical across providers.)
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToUtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>()
            .HaveConversion<DateTimeOffsetToUtcTicksConverter>();

        base.ConfigureConventions(configurationBuilder);
    }
}
