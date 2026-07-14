using MediatR;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Todos.Commands.CreateTodo;

public class CreateTodoCommandHandler : IRequestHandler<CreateTodoCommand, TodoItemDto>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public CreateTodoCommandHandler(
        IApplicationDbContext context,
        ICurrentUserService currentUser,
        IDateTimeProvider dateTime)
    {
        _context = context;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<TodoItemDto> Handle(CreateTodoCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        // A supplied category must belong to the current user.
        if (request.CategoryId is int categoryId &&
            !await _context.Categories.AnyAsync(c => c.Id == categoryId && c.UserId == userId, cancellationToken))
        {
            throw new NotFoundException(nameof(Category), categoryId);
        }

        var entity = new TodoItem(
            userId, request.Title, request.Description, request.Priority, request.CategoryId, request.DueDate, _dateTime.UtcNow);

        _context.TodoItems.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return TodoItemDto.FromEntity(entity);
    }
}
