namespace TodoApp.IntegrationTests;

// Lightweight mirrors of the API's JSON responses for deserialization in tests.
// System.Net.Http.Json uses web (camelCase, case-insensitive) defaults, so these bind.

public record AuthResult(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserResult User);

public record UserResult(int Id, string Email, string Role);

public record CategoryResult(int Id, string Name, string Color);

public record TodoResult(
    int Id,
    string Title,
    string? Description,
    bool IsCompleted,
    int Status,
    string StatusName,
    int? CategoryId,
    int Priority,
    Guid ConcurrencyToken);
