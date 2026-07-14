using MediatR;

namespace TodoApp.Application.Todos.Commands.DeleteTodo;

public record DeleteTodoCommand(int Id) : IRequest;
