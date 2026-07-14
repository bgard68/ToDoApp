using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;

namespace TodoApp.Application.Todos.Dtos;

/// <summary>
/// Read model returned to API clients. The client resolves the category's name/color from
/// its own category list using <see cref="CategoryId"/>.
/// </summary>
public record TodoItemDto
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsCompleted { get; init; }
    public TodoStatus Status { get; init; }
    public string StatusName { get; init; } = string.Empty;
    public int? CategoryId { get; init; }
    public Priority Priority { get; init; }
    public string PriorityName { get; init; } = string.Empty;
    public DateTimeOffset? DueDate { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Send this back on update so the server can detect concurrent modifications.</summary>
    public Guid ConcurrencyToken { get; init; }

    public static TodoItemDto FromEntity(TodoItem item) => new()
    {
        Id = item.Id,
        Title = item.Title,
        Description = item.Description,
        IsCompleted = item.IsCompleted,
        Status = item.Status,
        StatusName = item.Status.ToString(),
        CategoryId = item.CategoryId,
        Priority = item.Priority,
        PriorityName = item.Priority.ToString(),
        DueDate = item.DueDate,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        ConcurrencyToken = item.ConcurrencyToken
    };
}
