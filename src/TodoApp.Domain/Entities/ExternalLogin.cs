using TodoApp.Domain.Common;

namespace TodoApp.Domain.Entities;

/// <summary>
/// Links a <see cref="User"/> to an external identity provider (e.g. Google). Modeled after the
/// AspNetUserLogins pattern so more providers can be added without schema changes.
/// </summary>
public class ExternalLogin : BaseEntity
{
    public int UserId { get; private set; }

    /// <summary>Provider name, e.g. "Google".</summary>
    public string Provider { get; private set; } = string.Empty;

    /// <summary>The provider's stable subject identifier for the user.</summary>
    public string ProviderKey { get; private set; } = string.Empty;

    private ExternalLogin() { }

    public ExternalLogin(int userId, string provider, string providerKey, DateTimeOffset now)
    {
        if (userId <= 0)
        {
            throw new ArgumentException("A valid user is required.", nameof(userId));
        }

        UserId = userId;
        Provider = !string.IsNullOrWhiteSpace(provider)
            ? provider
            : throw new ArgumentException("Provider is required.", nameof(provider));
        ProviderKey = !string.IsNullOrWhiteSpace(providerKey)
            ? providerKey
            : throw new ArgumentException("Provider key is required.", nameof(providerKey));
        CreatedAt = now;
    }
}
