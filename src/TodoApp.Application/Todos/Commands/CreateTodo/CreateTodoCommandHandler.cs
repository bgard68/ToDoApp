using MediatR;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Todos.Commands.CreateTodo;

public class CreateTodoCommandHandler : IRequestHandler<CreateTodoCommand, TodoItemDto>
{
    private readonly ITodoRepository _todos;
    private readonly ICategoryRepository _categories;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public CreateTodoCommandHandler(
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

    public async Task<TodoItemDto> Handle(CreateTodoCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        // A supplied category must belong to the current user.
        if (request.CategoryId is int categoryId &&
            !await _categories.ExistsAsync(categoryId, userId, cancellationToken))
        {
            throw new NotFoundException(nameof(Category), categoryId);
        }

        var entity = new TodoItem(
            userId, request.Title, request.Description, request.Priority, request.CategoryId, request.DueDate, _dateTime.UtcNow);

        await _todos.AddAsync(entity, cancellationToken);

        return TodoItemDto.FromEntity(entity);
    }
}
