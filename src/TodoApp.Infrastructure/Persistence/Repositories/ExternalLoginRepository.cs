using System.Data.Common;
using Dapper;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Infrastructure.Persistence.Repositories;

public sealed class ExternalLoginRepository : RepositoryBase, IExternalLoginRepository
{
    private const string Columns = "Id, UserId, Provider, ProviderKey, CreatedAt, UpdatedAt";

    public ExternalLoginRepository(IDbConnectionContext context, IDbConnectionFactory factory)
        : base(context, factory)
    {
    }

    public async Task<ExternalLogin?> GetByProviderKeyAsync(
        string provider, string providerKey, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Columns} FROM ExternalLogins WHERE Provider = @Provider AND ProviderKey = @ProviderKey";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<ExternalLogin>(
            Command(sql, new { Provider = provider, ProviderKey = providerKey }, cancellationToken));
    }

    public async Task<int> AddAsync(ExternalLogin login, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO ExternalLogins (UserId, Provider, ProviderKey, CreatedAt, UpdatedAt)
            VALUES (@UserId, @Provider, @ProviderKey, @CreatedAt, @UpdatedAt)
            """;

        try
        {
            var id = await InsertAsync(sql, new
            {
                login.UserId,
                login.Provider,
                login.ProviderKey,
                login.CreatedAt,
                login.UpdatedAt
            }, cancellationToken);
            login.Id = id;
            return id;
        }
        catch (DbException ex) when (ex.IsUniqueConstraintViolation())
        {
            throw new DuplicateKeyException("This external login is already linked to an account.", ex);
        }
    }

    public async Task<int> CountForUserAsync(int userId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(1) FROM ExternalLogins WHERE UserId = @UserId";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(Command(sql, new { UserId = userId }, cancellationToken));
    }
}
