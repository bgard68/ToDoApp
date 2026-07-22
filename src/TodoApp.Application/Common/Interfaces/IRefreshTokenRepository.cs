using TodoApp.Domain.Entities;

namespace TodoApp.Application.Common.Interfaces;

/// <summary>Persistence operations for <see cref="RefreshToken"/> records.</summary>
public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>All not-yet-revoked tokens for a user (used to revoke every active session).</summary>
    Task<IReadOnlyList<RefreshToken>> GetUnrevokedForUserAsync(int userId, CancellationToken cancellationToken);

    /// <summary>Inserts the token and assigns its generated Id.</summary>
    Task<int> AddAsync(RefreshToken token, CancellationToken cancellationToken);

    /// <summary>Persists revocation state (revoked-at/reason, replaced-by hash).</summary>
    Task UpdateAsync(RefreshToken token, CancellationToken cancellationToken);
}
