using MediatR;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Todos.Commands.DeleteTodo;

public class DeleteTodoCommandHandler : IRequestHandler<DeleteTodoCommand>
{
    private readonly ITodoRepository _todos;
    private readonly ICurrentUserService _currentUser;

    public DeleteTodoCommandHandler(ITodoRepository todos, ICurrentUserService currentUser)
    {
        _todos = todos;
        _currentUser = currentUser;
    }

    public async Task Handle(DeleteTodoCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var entity = await _todos.GetByIdAsync(request.Id, userId, cancellationToken)
            ?? throw new NotFoundException(nameof(TodoItem), request.Id);

        await _todos.DeleteAsync(entity.Id, userId, cancellationToken);
    }
}
