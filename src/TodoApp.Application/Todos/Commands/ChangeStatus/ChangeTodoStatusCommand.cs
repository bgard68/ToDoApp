using MediatR;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Enums;

namespace TodoApp.Application.Todos.Commands.ChangeStatus;

/// <summary>Moves a task to another lane (drag-and-drop on the board).</summary>
public record ChangeTodoStatusCommand : IRequest<TodoItemDto>
{
    public int Id { get; init; }
    public TodoStatus Status { get; init; }
    public Guid? ConcurrencyToken { get; init; }
}
