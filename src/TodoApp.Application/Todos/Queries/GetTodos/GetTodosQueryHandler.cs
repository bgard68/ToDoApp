using MediatR;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Enums;

namespace TodoApp.Application.Todos.Queries.GetTodos;

public class GetTodosQueryHandler : IRequestHandler<GetTodosQuery, IReadOnlyList<TodoItemDto>>
{
    private readonly IApplicationDbContext _context;
    private readonly ICurrentUserService _currentUser;

    public GetTodosQueryHandler(IApplicationDbContext context, ICurrentUserService currentUser)
    {
        _context = context;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<TodoItemDto>> Handle(GetTodosQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var query = _context.TodoItems
            .AsNoTracking()
            .Where(t => t.UserId == userId);

        query = request.Filter switch
        {
            TodoFilter.Active => query.Where(t => t.Status != TodoStatus.Done),
            TodoFilter.Completed => query.Where(t => t.Status == TodoStatus.Done),
            _ => query
        };

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(t =>
                t.Title.Contains(term) ||
                (t.Description != null && t.Description.Contains(term)));
        }

        // Ordered so each lane reads high-priority / soonest-due first; the board buckets by status.
        var entities = await query
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.DueDate == null)
            .ThenBy(t => t.DueDate)
            .ThenByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        return entities.Select(TodoItemDto.FromEntity).ToList();
    }
}
