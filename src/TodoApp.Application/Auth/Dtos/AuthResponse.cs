namespace TodoApp.Application.Auth.Dtos;

/// <summary>
/// Returned on register/login/refresh. The refresh token is delivered in plaintext
/// exactly once here; the server only ever stores its hash.
/// </summary>
public record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserDto User);
