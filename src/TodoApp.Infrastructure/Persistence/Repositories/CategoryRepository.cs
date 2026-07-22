using System.Data.Common;
using Dapper;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Infrastructure.Persistence.Repositories;

public sealed class CategoryRepository : RepositoryBase, ICategoryRepository
{
    private const string Columns = "Id, UserId, Name, Color, CreatedAt, UpdatedAt";

    public CategoryRepository(IDbConnectionContext context, IDbConnectionFactory factory)
        : base(context, factory)
    {
    }

    public async Task<IReadOnlyList<Category>> GetForUserAsync(int userId, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Columns} FROM Categories WHERE UserId = @UserId ORDER BY Name";
        var connection = await ConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Category>(Command(sql, new { UserId = userId }, cancellationToken));
        return rows.ToList();
    }

    public async Task<bool> ExistsAsync(int id, int userId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(1) FROM Categories WHERE Id = @Id AND UserId = @UserId";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            Command(sql, new { Id = id, UserId = userId }, cancellationToken)) > 0;
    }

    public async Task<Category?> GetByIdAsync(int id, int userId, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Columns} FROM Categories WHERE Id = @Id AND UserId = @UserId";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Category>(
            Command(sql, new { Id = id, UserId = userId }, cancellationToken));
    }

    public async Task<int> AddAsync(Category category, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO Categories (UserId, Name, Color, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Name, @Color, @CreatedAt, @UpdatedAt)
            """;

        try
        {
            var id = await InsertAsync(sql, ToParameters(category), cancellationToken);
            category.Id = id;
            return id;
        }
        catch (DbException ex) when (ex.IsUniqueConstraintViolation())
        {
            throw new DuplicateKeyException("A category with this name already exists.", ex);
        }
    }

    public async Task AddRangeAsync(IEnumerable<Category> categories, CancellationToken cancellationToken)
    {
        foreach (var category in categories)
        {
            await AddAsync(category, cancellationToken);
        }
    }

    public async Task UpdateAsync(Category category, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE Categories SET Name = @Name, Color = @Color, UpdatedAt = @UpdatedAt
            WHERE Id = @Id AND UserId = @UserId
            """;

        try
        {
            var connection = await ConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(Command(sql, new
            {
                category.Id,
                category.UserId,
                category.Name,
                category.Color,
                category.UpdatedAt
            }, cancellationToken));
        }
        catch (DbException ex) when (ex.IsUniqueConstraintViolation())
        {
            throw new DuplicateKeyException("A category with this name already exists.", ex);
        }
    }

    public async Task DeleteAsync(int id, int userId, CancellationToken cancellationToken)
    {
        const string sql = "DELETE FROM Categories WHERE Id = @Id AND UserId = @UserId";
        var connection = await ConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(Command(sql, new { Id = id, UserId = userId }, cancellationToken));
    }

    private static object ToParameters(Category category) => new
    {
        category.UserId,
        category.Name,
        category.Color,
        category.CreatedAt,
        category.UpdatedAt
    };
}
