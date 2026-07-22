using Dapper;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Infrastructure.Persistence.Repositories;

public sealed class RefreshTokenRepository : RepositoryBase, IRefreshTokenRepository
{
    private const string Columns =
        "Id, UserId, TokenHash, ExpiresAt, RevokedAt, RevokedReason, ReplacedByTokenHash, CreatedAt, UpdatedAt";

    public RefreshTokenRepository(IDbConnectionContext context, IDbConnectionFactory factory)
        : base(context, factory)
    {
    }

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Columns} FROM RefreshTokens WHERE TokenHash = @TokenHash";
        var connection = await ConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<RefreshToken>(
            Command(sql, new { TokenHash = tokenHash }, cancellationToken));
    }

    public async Task<IReadOnlyList<RefreshToken>> GetUnrevokedForUserAsync(int userId, CancellationToken cancellationToken)
    {
        const string sql = $"SELECT {Columns} FROM RefreshTokens WHERE UserId = @UserId AND RevokedAt IS NULL";
        var connection = await ConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<RefreshToken>(Command(sql, new { UserId = userId }, cancellationToken));
        return rows.ToList();
    }

    public async Task<int> AddAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO RefreshTokens (UserId, TokenHash, ExpiresAt, RevokedAt, RevokedReason, ReplacedByTokenHash, CreatedAt, UpdatedAt)
            VALUES (@UserId, @TokenHash, @ExpiresAt, @RevokedAt, @RevokedReason, @ReplacedByTokenHash, @CreatedAt, @UpdatedAt)
            """;

        var id = await InsertAsync(sql, new
        {
            token.UserId,
            token.TokenHash,
            token.ExpiresAt,
            token.RevokedAt,
            token.RevokedReason,
            token.ReplacedByTokenHash,
            token.CreatedAt,
            token.UpdatedAt
        }, cancellationToken);
        token.Id = id;
        return id;
    }

    public async Task UpdateAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE RefreshTokens SET
                RevokedAt = @RevokedAt, RevokedReason = @RevokedReason,
                ReplacedByTokenHash = @ReplacedByTokenHash, UpdatedAt = @UpdatedAt
            WHERE Id = @Id
            """;

        var connection = await ConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(Command(sql, new
        {
            token.Id,
            token.RevokedAt,
            token.RevokedReason,
            token.ReplacedByTokenHash,
            token.UpdatedAt
        }, cancellationToken));
    }
}
