using MediatR;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Enums;

namespace TodoApp.Application.Todos.Commands.UpdateTodo;

public record UpdateTodoCommand : IRequest<TodoItemDto>
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public Priority Priority { get; init; } = Priority.Medium;
    public int? CategoryId { get; init; }
    public DateTimeOffset? DueDate { get; init; }

    /// <summary>The token the client last saw. When provided, a stale value causes a 409 conflict.</summary>
    public Guid? ConcurrencyToken { get; init; }
}
