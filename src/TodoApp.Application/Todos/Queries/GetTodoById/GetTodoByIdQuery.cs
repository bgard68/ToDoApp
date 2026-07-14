using MediatR;
using TodoApp.Application.Todos.Dtos;

namespace TodoApp.Application.Todos.Queries.GetTodoById;

public record GetTodoByIdQuery(int Id) : IRequest<TodoItemDto>;
