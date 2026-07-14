namespace TodoApp.Application.Common.Models;

/// <summary>A freshly issued access token and its metadata.</summary>
public record AccessToken(string Token, DateTimeOffset ExpiresAt, string Jti);

/// <summary>
/// A freshly generated refresh token. <see cref="RawToken"/> is returned to the client
/// exactly once; only <see cref="TokenHash"/> is persisted.
/// </summary>
public record RefreshTokenResult(string RawToken, string TokenHash, DateTimeOffset ExpiresAt);
