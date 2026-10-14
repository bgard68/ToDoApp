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
    /// Translates the two save failures a handler can act on into layer-neutral exceptions —
    /// <see cref="ConcurrencyViolationException"/> for a stale row and
    /// <see cref="UniqueConstraintViolationException"/> for a provider-specific uniqueness error —
    /// and leaves every other save failure alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both public <c>SaveChangesAsync</c> overloads funnel through this one, as does
    /// <see cref="SaveChanges(bool)"/>, so neither translation can be bypassed by calling a
    /// different overload. Nothing here swallows an exception: a failure that is neither of the
    /// two propagates untouched, and both of those keep the provider's error as their inner
    /// exception.
    /// </para>
    /// <para>
    /// The concurrency catch comes first because <c>DbUpdateConcurrencyException</c> derives from
    /// <c>DbUpdateException</c>; ordered the other way the uniqueness filter would see it first.
    /// That filter rejects it anyway — see <see cref="UniqueConstraintDetector"/> — so the order
    /// is belt and braces rather than the only thing keeping them apart.
    /// </para>
    /// </remarks>
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyViolationException(exception);
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
        catch (DbUpdateConcurrencyException exception)
        {
            throw new ConcurrencyViolationException(exception);
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
