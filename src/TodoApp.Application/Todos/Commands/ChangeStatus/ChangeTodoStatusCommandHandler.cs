using MediatR;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Todos.Commands.ChangeStatus;

public class ChangeTodoStatusCommandHandler : IRequestHandler<ChangeTodoStatusCommand, TodoItemDto>
{
    private readonly ITodoRepository _todos;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public ChangeTodoStatusCommandHandler(
        ITodoRepository todos,
        ICurrentUserService currentUser,
        IDateTimeProvider dateTime)
    {
        _todos = todos;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<TodoItemDto> Handle(ChangeTodoStatusCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var entity = await _todos.GetByIdAsync(request.Id, userId, cancellationToken)
            ?? throw new NotFoundException(nameof(TodoItem), request.Id);

        var expectedToken = request.ConcurrencyToken is Guid clientToken && clientToken != Guid.Empty
            ? clientToken
            : entity.ConcurrencyToken;

        entity.MoveTo(request.Status, _dateTime.UtcNow);

        if (!await _todos.UpdateAsync(entity, expectedToken, cancellationToken))
        {
            var current = await _todos.GetByIdAsync(request.Id, userId, cancellationToken);

            throw new ConcurrencyConflictException(
                "This item was modified by someone else. Reload and try again.",
                current is null ? null : TodoItemDto.FromEntity(current));
        }

        return TodoItemDto.FromEntity(entity);
    }
}
