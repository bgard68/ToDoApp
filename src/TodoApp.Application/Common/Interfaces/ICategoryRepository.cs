using TodoApp.Domain.Entities;

namespace TodoApp.Application.Common.Interfaces;

/// <summary>Persistence operations for <see cref="Category"/> aggregates, scoped to their owner.</summary>
public interface ICategoryRepository
{
    Task<IReadOnlyList<Category>> GetForUserAsync(int userId, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(int id, int userId, CancellationToken cancellationToken);

    Task<Category?> GetByIdAsync(int id, int userId, CancellationToken cancellationToken);

    /// <summary>Inserts the category and assigns its generated Id. Throws
    /// <see cref="Common.Exceptions.DuplicateKeyException"/> on a duplicate (UserId, Name).</summary>
    Task<int> AddAsync(Category category, CancellationToken cancellationToken);

    /// <summary>Inserts several categories (used to seed a new user's defaults).</summary>
    Task AddRangeAsync(IEnumerable<Category> categories, CancellationToken cancellationToken);

    /// <summary>Updates name/color. Throws <see cref="Common.Exceptions.DuplicateKeyException"/>
    /// on a duplicate (UserId, Name).</summary>
    Task UpdateAsync(Category category, CancellationToken cancellationToken);

    /// <summary>Deletes the category; referencing todos are left uncategorized (FK ON DELETE SET NULL).</summary>
    Task DeleteAsync(int id, int userId, CancellationToken cancellationToken);
}
