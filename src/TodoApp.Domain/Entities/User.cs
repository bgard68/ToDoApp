using TodoApp.Domain.Common;
using TodoApp.Domain.Enums;

namespace TodoApp.Domain.Entities;

/// <summary>
/// An application user. The <see cref="SecurityStamp"/> is the linchpin of token revocation:
/// it is embedded in every access token and validated on each request, so rotating it instantly
/// invalidates all outstanding access tokens. Timestamps are supplied by the caller.
/// </summary>
public class User : BaseEntity
{
    public string Email { get; private set; } = string.Empty;

    /// <summary>Null for accounts that only sign in through an external provider.</summary>
    public string? PasswordHash { get; private set; }

    public UserRole Role { get; private set; } = UserRole.User;

    /// <summary>Random value stamped into access tokens; rotate to revoke them all.</summary>
    public string SecurityStamp { get; private set; } = NewStamp();

    public bool IsActive { get; private set; } = true;

    private User() { }

    public User(string email, string passwordHash, DateTimeOffset now, UserRole role = UserRole.User)
    {
        Email = NormalizeEmail(email);
        PasswordHash = passwordHash ?? throw new ArgumentNullException(nameof(passwordHash));
        Role = role;
        SecurityStamp = NewStamp();
        IsActive = true;
        CreatedAt = now;
    }

    /// <summary>Creates an account that authenticates only via an external provider (no local password).</summary>
    public static User CreateExternal(string email, DateTimeOffset now, UserRole role = UserRole.User)
    {
        return new User
        {
            Email = NormalizeEmail(email),
            PasswordHash = null,
            Role = role,
            CreatedAt = now
        };
    }

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    public void SetPassword(string passwordHash, DateTimeOffset now)
    {
        PasswordHash = passwordHash ?? throw new ArgumentNullException(nameof(passwordHash));
        RotateSecurityStamp(now);
    }

    /// <summary>
    /// Invalidates every access token previously issued to this user by changing the stamp
    /// they were signed with. Call on compromise, password change, or "sign out everywhere".
    /// </summary>
    public void RotateSecurityStamp(DateTimeOffset now)
    {
        SecurityStamp = NewStamp();
        Touch(now);
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        RotateSecurityStamp(now);
    }

    public void Activate(DateTimeOffset now)
    {
        IsActive = true;
        Touch(now);
    }

    private void Touch(DateTimeOffset now) => UpdatedAt = now;

    public static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email is required.", nameof(email));
        }

        return email.Trim().ToLowerInvariant();
    }

    private static string NewStamp() => Guid.NewGuid().ToString("N");
}
