using System.Text;
using Dapper;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Todos.Queries.GetTodos;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;

namespace TodoApp.Infrastructure.Persistence.Repositories;

public sealed class TodoRepository : RepositoryBase, ITodoRepository
{
    private const string Columns =
        "Id, UserId, Title, Description, Status, CategoryId, Priority, DueDate, ConcurrencyToken, CreatedAt, UpdatedAt";

    public TodoRepository(IDbConnectionContext context, IDbConnectionFactory factory)
        : base(context, factory)
    {
    }

    public async Task<IReadOnlyList<TodoItem>> GetForUserAsync(
        int userId, TodoFilter filter, string? search, CancellationToken cancellationToken)
    {
        var sql = new StringBuilder($"SELECT {Columns} FROM TodoItems WHERE UserId = @UserId");
        var parameters = new DynamicParameters();
        parameters.Add("UserId", userId);

        if (filter is TodoFilter.Active or TodoFilter.Completed)
        {
            sql.Append(filter == TodoFilter.Active ? " AND Status <> @Done" : " AND Status = @Done");
            parameters.Add("Done", (int)TodoStatus.Done);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            sql.Append(@" AND (Title LIKE @Search ESCAPE '\' OR (Description IS NOT NULL AND Description LIKE @Search ESCAPE '\'))");
            parameters.Add("Search", $"%{EscapeLike(search.Trim())}%");
        }

        // High priority first, then soonest due (nulls last), then newest — the board buckets by status.
        sql.Append(" ORDER BY Priority DESC, CASE WHEN DueDate IS NULL THEN 1 ELSE 0 END, DueDate, CreatedAt DESC");

        var connection = await ConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TodoItem>(Command(sql.ToString(), parameters, cancellationToken));
        return rows.ToList();
    }

    public async Task<TodoItem?> GetByIdAsync(int id, int userId, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Columns} FROM TodoItems WHERE Id = @Id AND UserId = @UserId";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<TodoItem>(
            Command(sql, new { Id = id, UserId = userId }, cancellationToken));
    }

    public async Task<int> AddAsync(TodoItem todo, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO TodoItems (UserId, Title, Description, Status, CategoryId, Priority, DueDate, ConcurrencyToken, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Title, @Description, @Status, @CategoryId, @Priority, @DueDate, @ConcurrencyToken, @CreatedAt, @UpdatedAt)
            """;

        var id = await InsertAsync(sql, ToParameters(todo), cancellationToken);
        todo.Id = id;
        return id;
    }

    public async Task<bool> UpdateAsync(TodoItem todo, Guid expectedToken, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE TodoItems SET
                Title = @Title, Description = @Description, Status = @Status, CategoryId = @CategoryId,
                Priority = @Priority, DueDate = @DueDate, ConcurrencyToken = @ConcurrencyToken, UpdatedAt = @UpdatedAt
            WHERE Id = @Id AND UserId = @UserId AND ConcurrencyToken = @ExpectedToken
            """;

        var parameters = new DynamicParameters(ToParameters(todo));
        parameters.Add("Id", todo.Id);
        parameters.Add("ExpectedToken", expectedToken);

        var connection = await ConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(Command(sql, parameters, cancellationToken));
        return affected > 0;
    }

    public async Task DeleteAsync(int id, int userId, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM TodoItems WHERE Id = @Id AND UserId = @UserId";
        var connection = await ConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(Command(sql, new { Id = id, UserId = userId }, cancellationToken));
    }

    private static object ToParameters(TodoItem todo) => new
    {
        todo.UserId,
        todo.Title,
        todo.Description,
        Status = (int)todo.Status,
        todo.CategoryId,
        Priority = (int)todo.Priority,
        todo.DueDate,
        todo.ConcurrencyToken,
        todo.CreatedAt,
        todo.UpdatedAt
    };

    // Escape LIKE wildcards so a search term is matched literally (ESCAPE '\').
    private static string EscapeLike(string term) => term
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");
}
