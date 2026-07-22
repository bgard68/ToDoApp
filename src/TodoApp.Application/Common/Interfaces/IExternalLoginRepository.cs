using TodoApp.Domain.Entities;

namespace TodoApp.Application.Common.Interfaces;

/// <summary>Persistence operations linking users to external identity providers.</summary>
public interface IExternalLoginRepository
{
    Task<ExternalLogin?> GetByProviderKeyAsync(string provider, string providerKey, CancellationToken cancellationToken);

    /// <summary>Inserts the link and assigns its generated Id. Throws
    /// <see cref="Common.Exceptions.DuplicateKeyException"/> on a duplicate (Provider, ProviderKey).</summary>
    Task<int> AddAsync(ExternalLogin login, CancellationToken cancellationToken);

    Task<int> CountForUserAsync(int userId, CancellationToken cancellationToken);
}
