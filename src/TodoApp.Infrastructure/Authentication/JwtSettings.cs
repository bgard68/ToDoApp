namespace TodoApp.Infrastructure.Authentication;

public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "TodoApp";

    public string Audience { get; set; } = "TodoApp";

    /// <summary>Signing key. MUST be overridden in production via env var or user-secrets.</summary>
    public string Key { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 7;
}
