using TodoApp.Domain.Common;

namespace TodoApp.Domain.Entities;

/// <summary>
/// A single-use refresh token. Only the SHA-256 hash of the token is stored. Expiry/active
/// checks take the current time as a parameter so behavior is deterministic under test.
/// </summary>
public class RefreshToken : BaseEntity
{
    public int UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    /// <summary>Hash of the token that replaced this one (rotation chain, for reuse detection).</summary>
    public string? ReplacedByTokenHash { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsExpired(now);

    private RefreshToken() { }

    public RefreshToken(int userId, string tokenHash, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        UserId = userId;
        TokenHash = tokenHash ?? throw new ArgumentNullException(nameof(tokenHash));
        ExpiresAt = expiresAt;
        CreatedAt = now;
    }

    public void Revoke(string reason, DateTimeOffset now, string? replacedByTokenHash = null)
    {
        if (IsRevoked)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
        ReplacedByTokenHash = replacedByTokenHash;
    }
}
