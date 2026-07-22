using MediatR;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Todos.Queries.GetTodoById;

public class GetTodoByIdQueryHandler : IRequestHandler<GetTodoByIdQuery, TodoItemDto>
{
    private readonly ITodoRepository _todos;
    private readonly ICurrentUserService _currentUser;

    public GetTodoByIdQueryHandler(ITodoRepository todos, ICurrentUserService currentUser)
    {
        _todos = todos;
        _currentUser = currentUser;
    }

    public async Task<TodoItemDto> Handle(GetTodoByIdQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var entity = await _todos.GetByIdAsync(request.Id, userId, cancellationToken)
            ?? throw new NotFoundException(nameof(TodoItem), request.Id);

        return TodoItemDto.FromEntity(entity);
    }
}
