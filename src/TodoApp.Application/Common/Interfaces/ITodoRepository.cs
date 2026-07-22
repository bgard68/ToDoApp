using TodoApp.Application.Todos.Queries.GetTodos;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Common.Interfaces;

/// <summary>Persistence operations for <see cref="TodoItem"/> aggregates, scoped to their owner.</summary>
public interface ITodoRepository
{
    /// <summary>Board items for a user, filtered/searched and ordered for the Kanban lanes.</summary>
    Task<IReadOnlyList<TodoItem>> GetForUserAsync(
        int userId, TodoFilter filter, string? search, CancellationToken cancellationToken);

    Task<TodoItem?> GetByIdAsync(int id, int userId, CancellationToken cancellationToken);

    /// <summary>Inserts the item and assigns its generated <see cref="Domain.Common.BaseEntity.Id"/>.</summary>
    Task<int> AddAsync(TodoItem todo, CancellationToken cancellationToken);

    /// <summary>
    /// Updates all mutable columns, guarding on the optimistic-concurrency token. Returns false
    /// when no row matched the expected token (a concurrent modification), true on success.
    /// </summary>
    Task<bool> UpdateAsync(TodoItem todo, Guid expectedToken, CancellationToken cancellationToken);

    Task DeleteAsync(int id, int userId, CancellationToken cancellationToken);
}
