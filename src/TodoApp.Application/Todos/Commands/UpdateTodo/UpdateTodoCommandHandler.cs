using MediatR;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Todos.Commands.UpdateTodo;

public class UpdateTodoCommandHandler : IRequestHandler<UpdateTodoCommand, TodoItemDto>
{
    private readonly ITodoRepository _todos;
    private readonly ICategoryRepository _categories;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public UpdateTodoCommandHandler(
        ITodoRepository todos,
        ICategoryRepository categories,
        ICurrentUserService currentUser,
        IDateTimeProvider dateTime)
    {
        _todos = todos;
        _categories = categories;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<TodoItemDto> Handle(UpdateTodoCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var entity = await _todos.GetByIdAsync(request.Id, userId, cancellationToken)
            ?? throw new NotFoundException(nameof(TodoItem), request.Id);

        if (request.CategoryId is int categoryId &&
            !await _categories.ExistsAsync(categoryId, userId, cancellationToken))
        {
            throw new NotFoundException(nameof(Category), categoryId);
        }

        // Guard on the client's token when supplied; otherwise on the value we just loaded.
        var expectedToken = request.ConcurrencyToken is Guid clientToken && clientToken != Guid.Empty
            ? clientToken
            : entity.ConcurrencyToken;

        entity.Update(request.Title, request.Description, request.Priority, request.CategoryId, request.DueDate, _dateTime.UtcNow);

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
