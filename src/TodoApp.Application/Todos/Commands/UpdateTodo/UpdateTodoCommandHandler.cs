using MediatR;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Todos.Commands.UpdateTodo;

public class UpdateTodoCommandHandler : IRequestHandler<UpdateTodoCommand, TodoItemDto>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public UpdateTodoCommandHandler(
        IApplicationDbContext context,
        ICurrentUserService currentUser,
        IDateTimeProvider dateTime)
    {
        _context = context;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<TodoItemDto> Handle(UpdateTodoCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var entity = await _context.TodoItems
            .FirstOrDefaultAsync(t => t.Id == request.Id && t.UserId == userId, cancellationToken)
            ?? throw new NotFoundException(nameof(TodoItem), request.Id);

        if (request.CategoryId is int categoryId &&
            !await _context.Categories.AnyAsync(c => c.Id == categoryId && c.UserId == userId, cancellationToken))
        {
            throw new NotFoundException(nameof(Category), categoryId);
        }

        if (request.ConcurrencyToken is Guid clientToken && clientToken != Guid.Empty)
        {
            _context.SetOriginalConcurrencyToken(entity, clientToken);
        }

        entity.Update(request.Title, request.Description, request.Priority, request.CategoryId, request.DueDate, _dateTime.UtcNow);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var current = await _context.TodoItems
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == request.Id && t.UserId == userId, cancellationToken);

            throw new ConcurrencyConflictException(
                "This item was modified by someone else. Reload and try again.",
                current is null ? null : TodoItemDto.FromEntity(current));
        }

        return TodoItemDto.FromEntity(entity);
    }
}
