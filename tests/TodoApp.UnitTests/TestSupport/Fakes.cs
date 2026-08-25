using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Common.Models;
using TodoApp.Domain.Entities;
using TodoApp.Infrastructure.Persistence;

namespace TodoApp.UnitTests.TestSupport;

/// <summary>Controllable clock for deterministic time-dependent tests.</summary>
public sealed class FakeDateTimeProvider : IDateTimeProvider
{
    public FakeDateTimeProvider()
    {
    }

    public FakeDateTimeProvider(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FakeCurrentUserService : ICurrentUserService
{
    public int? UserId { get; set; }
    public string? Email { get; set; }
    public string? Role { get; set; }

    public bool IsAuthenticated => UserId is not null;

    public bool IsInRole(string role) => string.Equals(Role, role, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Deterministic token service for tests. HashToken is consistent with CreateRefreshToken
/// so the refresh/rotation flow can be exercised without real crypto.
/// </summary>
public sealed class FakeJwtTokenService : IJwtTokenService
{
    private int _counter;

    public AccessToken CreateAccessToken(User user) => new(
        $"access-{user.Id}-{user.SecurityStamp}",
        DateTimeOffset.UtcNow.AddMinutes(15),
        Guid.NewGuid().ToString("N"));

    public RefreshTokenResult CreateRefreshToken()
    {
        var raw = $"rt-{++_counter}-{Guid.NewGuid():N}";
        return new RefreshTokenResult(raw, HashToken(raw), DateTimeOffset.UtcNow.AddDays(7));
    }

    public string HashToken(string rawToken) => $"H:{rawToken}";
}

public sealed class FakeGoogleTokenValidator : IGoogleTokenValidator
{
    public GoogleUserInfo? Result { get; set; }

    public Task<GoogleUserInfo?> ValidateAsync(string idToken, CancellationToken cancellationToken)
        => Task.FromResult(Result);
}

/// <summary>
/// Controllable breached-password checker (review finding L9). Defaults to "not breached" so
/// existing tests are unaffected; set <see cref="Breached"/> to exercise the rejection path.
/// Tests never touch the network.
/// </summary>
public sealed class FakeBreachedPasswordChecker : IBreachedPasswordChecker
{
    public bool Breached { get; set; }

    public int CallCount { get; private set; }

    public Task<bool> IsBreachedAsync(string password, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(Breached);
    }
}

/// <summary>
/// Wraps a real context and runs a hook immediately before its first SaveChanges, so a test can
/// stage the other half of a race — another actor updating or deleting the row between this
/// handler's read and its write. Only the first save is intercepted; later saves pass straight
/// through, so a handler that saves twice still behaves normally after the conflict.
/// </summary>
public sealed class RacingDbContext : IApplicationDbContext
{
    private readonly ApplicationDbContext _inner;
    private Action? _beforeFirstSave;

    public RacingDbContext(ApplicationDbContext inner, Action beforeFirstSave)
    {
        _inner = inner;
        _beforeFirstSave = beforeFirstSave;
    }

    public DbSet<TodoItem> TodoItems => _inner.TodoItems;

    public DbSet<Category> Categories => _inner.Categories;

    public DbSet<User> Users => _inner.Users;

    public DbSet<RefreshToken> RefreshTokens => _inner.RefreshTokens;

    public DbSet<ExternalLogin> ExternalLogins => _inner.ExternalLogins;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        var hook = _beforeFirstSave;
        _beforeFirstSave = null;
        hook?.Invoke();
        return _inner.SaveChangesAsync(cancellationToken);
    }

    public void SetOriginalConcurrencyToken(TodoItem entity, Guid token)
        => _inner.SetOriginalConcurrencyToken(entity, token);
}
