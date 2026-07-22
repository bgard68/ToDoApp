using System.Data.Common;
using Dapper;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Infrastructure.Persistence.Repositories;

public sealed class UserRepository : RepositoryBase, IUserRepository
{
    private const string Columns = "Id, Email, PasswordHash, Role, SecurityStamp, IsActive, CreatedAt, UpdatedAt";

    public UserRepository(IDbConnectionContext context, IDbConnectionFactory factory)
        : base(context, factory)
    {
    }

    public async Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Columns} FROM Users WHERE Id = @Id";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<User>(Command(sql, new { Id = id }, cancellationToken));
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Columns} FROM Users WHERE Email = @Email";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<User>(Command(sql, new { Email = email }, cancellationToken));
    }

    public async Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(1) FROM Users WHERE Email = @Email";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(Command(sql, new { Email = email }, cancellationToken)) > 0;
    }

    public async Task<bool> AnyAsync(CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(1) FROM Users";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(Command(sql, null, cancellationToken)) > 0;
    }

    public async Task<int> AddAsync(User user, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO Users (Email, PasswordHash, Role, SecurityStamp, IsActive, CreatedAt, UpdatedAt)
            VALUES (@Email, @PasswordHash, @Role, @SecurityStamp, @IsActive, @CreatedAt, @UpdatedAt)
            """;

        try
        {
            var id = await InsertAsync(sql, ToParameters(user), cancellationToken);
            user.Id = id;
            return id;
        }
        catch (DbException ex) when (ex.IsUniqueConstraintViolation())
        {
            throw new DuplicateKeyException("An account with this email already exists.", ex);
        }
    }

    public async Task UpdateAsync(User user, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE Users SET
                Email = @Email, PasswordHash = @PasswordHash, Role = @Role,
                SecurityStamp = @SecurityStamp, IsActive = @IsActive, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;

        var parameters = new DynamicParameters(ToParameters(user));
        parameters.Add("Id", user.Id);

        var connection = await ConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(Command(sql, parameters, cancellationToken));
    }

    private static object ToParameters(User user) => new
    {
        user.Email,
        user.PasswordHash,
        Role = (int)user.Role,
        user.SecurityStamp,
        user.IsActive,
        user.CreatedAt,
        user.UpdatedAt
    };
}
