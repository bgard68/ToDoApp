using Microsoft.EntityFrameworkCore;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Common.Interfaces;

/// <summary>
/// Abstraction over the persistence context so the Application layer never
/// depends on Infrastructure/EF Core directly (dependency inversion).
/// </summary>
public interface IApplicationDbContext
{
    DbSet<TodoItem> TodoItems { get; }

    DbSet<Category> Categories { get; }

    DbSet<User> Users { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<ExternalLogin> ExternalLogins { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Overrides the original value EF Core uses for a todo's concurrency token in the UPDATE
    /// WHERE clause, so a stale token supplied by a disconnected client triggers a conflict.
    /// </summary>
    void SetOriginalConcurrencyToken(TodoItem entity, Guid token);
}
