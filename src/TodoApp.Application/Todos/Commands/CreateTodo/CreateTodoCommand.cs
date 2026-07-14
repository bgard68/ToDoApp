using MediatR;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Enums;

namespace TodoApp.Application.Todos.Commands.CreateTodo;

public record CreateTodoCommand : IRequest<TodoItemDto>
{
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public Priority Priority { get; init; } = Priority.Medium;
    public int? CategoryId { get; init; }
    public DateTimeOffset? DueDate { get; init; }
}
