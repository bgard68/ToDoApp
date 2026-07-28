namespace TodoApp.WebApi;

/// <summary>Fixed-window rate limit settings, bound from the <c>RateLimiting</c> config section.</summary>
/// <param name="PermitLimit">Requests allowed per window, per client.</param>
/// <param name="WindowSeconds">Length of the window in seconds.</param>
public record RateLimitOptions(int PermitLimit, int WindowSeconds);

/// <summary>Named rate-limiting policies.</summary>
public static class RateLimitPolicies
{
    /// <summary>Applied to the anonymous <c>/api/auth</c> endpoints (review finding H3).</summary>
    public const string Auth = "auth";
}
