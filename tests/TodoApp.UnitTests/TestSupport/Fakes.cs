using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Common.Models;
using TodoApp.Domain.Entities;

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
