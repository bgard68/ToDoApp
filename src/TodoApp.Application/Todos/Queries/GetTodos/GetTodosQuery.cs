using MediatR;
using TodoApp.Application.Todos.Dtos;

namespace TodoApp.Application.Todos.Queries.GetTodos;

public enum TodoFilter
{
    All = 0,
    Active = 1,
    Completed = 2
}

public record GetTodosQuery : IRequest<IReadOnlyList<TodoItemDto>>
{
    public TodoFilter Filter { get; init; } = TodoFilter.All;
    public string? Search { get; init; }
}
