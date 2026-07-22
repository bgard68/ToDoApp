using MediatR;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Todos.Dtos;

namespace TodoApp.Application.Todos.Queries.GetTodos;

public class GetTodosQueryHandler : IRequestHandler<GetTodosQuery, IReadOnlyList<TodoItemDto>>
{
    private readonly ITodoRepository _todos;
    private readonly ICurrentUserService _currentUser;

    public GetTodosQueryHandler(ITodoRepository todos, ICurrentUserService currentUser)
    {
        _todos = todos;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<TodoItemDto>> Handle(GetTodosQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var items = await _todos.GetForUserAsync(userId, request.Filter, request.Search, cancellationToken);

        return items.Select(TodoItemDto.FromEntity).ToList();
    }
}
