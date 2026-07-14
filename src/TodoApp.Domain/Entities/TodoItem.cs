using TodoApp.Domain.Common;
using TodoApp.Domain.Enums;

namespace TodoApp.Domain.Entities;

/// <summary>
/// A single task on the Kanban board, owned by a <see cref="User"/>. Timestamps are passed in
/// (sourced from IDateTimeProvider in the application layer) so the entity never reads the clock.
/// </summary>
public class TodoItem : BaseEntity
{
    public int UserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public TodoStatus Status { get; private set; } = TodoStatus.ToDo;

    /// <summary>Optional reference to a user-defined <see cref="Category"/>.</summary>
    public int? CategoryId { get; private set; }

    public Priority Priority { get; private set; } = Priority.Medium;

    public DateTimeOffset? DueDate { get; private set; }

    /// <summary>Optimistic-concurrency token; changes on every persisted modification.</summary>
    public Guid ConcurrencyToken { get; private set; } = Guid.NewGuid();

    /// <summary>True when the task is in the Done lane. Derived from <see cref="Status"/>; not stored.</summary>
    public bool IsCompleted => Status == TodoStatus.Done;

    // EF Core needs a parameterless constructor.
    private TodoItem() { }

    public TodoItem(
        int userId,
        string title,
        string? description,
        Priority priority,
        int? categoryId,
        DateTimeOffset? dueDate,
        DateTimeOffset now)
    {
        if (userId <= 0)
        {
            throw new ArgumentException("A valid owner is required.", nameof(userId));
        }

        UserId = userId;
        SetTitle(title);
        Description = Normalize(description);
        Priority = priority;
        CategoryId = categoryId;
        DueDate = dueDate;
        Status = TodoStatus.ToDo;
        CreatedAt = now;
    }

    public void Update(string title, string? description, Priority priority, int? categoryId, DateTimeOffset? dueDate, DateTimeOffset now)
    {
        SetTitle(title);
        Description = Normalize(description);
        Priority = priority;
        CategoryId = categoryId;
        DueDate = dueDate;
        Touch(now);
    }

    /// <summary>Moves the task to another lane (drag-and-drop on the board).</summary>
    public void MoveTo(TodoStatus status, DateTimeOffset now)
    {
        if (Status == status)
        {
            return;
        }

        Status = status;
        Touch(now);
    }

    private void SetTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title is required.", nameof(title));
        }

        Title = title.Trim();
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        ConcurrencyToken = Guid.NewGuid();
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
