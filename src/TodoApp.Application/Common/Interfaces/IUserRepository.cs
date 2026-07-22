using TodoApp.Domain.Entities;

namespace TodoApp.Application.Common.Interfaces;

/// <summary>Persistence operations for <see cref="User"/> accounts.</summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id, CancellationToken cancellationToken);

    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken);

    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    /// <summary>True when any user exists (used to decide whether to seed a fresh database).</summary>
    Task<bool> AnyAsync(CancellationToken cancellationToken);

    /// <summary>Inserts the user and assigns their generated Id. Throws
    /// <see cref="Common.Exceptions.DuplicateKeyException"/> on a duplicate email.</summary>
    Task<int> AddAsync(User user, CancellationToken cancellationToken);

    /// <summary>Persists mutable account fields (password hash, role, security stamp, active flag).</summary>
    Task UpdateAsync(User user, CancellationToken cancellationToken);
}
