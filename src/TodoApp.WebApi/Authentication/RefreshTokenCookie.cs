namespace TodoApp.WebApi.Authentication;

/// <summary>
/// Delivers the refresh token as an <c>httpOnly</c> cookie instead of a JSON field the SPA has to
/// store itself (review finding H2).
/// </summary>
/// <remarks>
/// <para>
/// The SPA previously kept the refresh token in <c>localStorage</c>, which any XSS can read — a
/// 7-day, silently-renewing session handed over in one line of injected script. An
/// <c>httpOnly</c> cookie is not reachable from JavaScript at all, so the same XSS can act as the
/// user only while it is running, and cannot exfiltrate anything that outlives the page.
/// </para>
/// <para>
/// <strong>SameSite=None is required, and is not a weakness here.</strong> The SPA
/// (<c>*.azurestaticapps.net</c>) and the API (<c>*.azurewebsites.net</c>) are different sites, so
/// a Lax/Strict cookie would simply never be sent. Same-origin would be the better answer, but it
/// needs the Static Web Apps linked-backend proxy, which is a Standard-tier feature — this stack
/// is Free tier. CSRF is handled instead by the API being pure JSON with a strict CORS allow-list
/// and no cookie-authenticated state-changing endpoint: the access token is a Bearer header, so a
/// cross-site form post carries the cookie but no <c>Authorization</c>, and the refresh endpoint
/// additionally requires its own double-submit check below.
/// </para>
/// <para>
/// The response body keeps returning the refresh token when
/// <c>Auth:RefreshTokenInBody</c> is enabled, so existing clients (and the smoke test) keep
/// working during the transition. It defaults to off in deployed environments.
/// </para>
/// </remarks>
public static class RefreshTokenCookie
{
    public const string Name = "todo_rt";

    /// <summary>
    /// CSRF defence: the caller must send this header. Any non-empty value will do.
    /// </summary>
    /// <remarks>
    /// A custom request header cannot be attached by a cross-site form post or image tag — the
    /// browser must first send a CORS preflight, and this API's policy only allows the SPA's
    /// origin. So the header's *presence* is the proof, not its value.
    ///
    /// The first implementation used a double-submit cookie whose value had to be echoed here.
    /// That is the textbook pattern and it is wrong for this deployment: the companion cookie is
    /// set on the API's domain, so the SPA — on a different domain — can never read it via
    /// document.cookie. The check was unsatisfiable, which would have silently broken silent
    /// re-authentication for every user. Verified against the live site before it caused harm.
    /// </remarks>
    public const string CsrfHeaderName = "X-Refresh-CSRF";

    /// <summary>Scope the cookie to the auth endpoints — nothing else needs to receive it.</summary>
    private const string Path = "/api/auth";

    public static void Write(HttpResponse response, string refreshToken, DateTimeOffset expiresAt)
    {
        response.Cookies.Append(Name, refreshToken, new CookieOptions
        {
            HttpOnly = true,                    // unreachable from JavaScript — the whole point
            Secure = true,                      // required with SameSite=None, and correct anyway
            SameSite = SameSiteMode.None,       // SPA and API are different sites (see remarks)
            Expires = expiresAt,
            Path = Path,
            IsEssential = true
        });
    }

    public static void Clear(HttpResponse response)
    {
        var expired = new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UnixEpoch,
            Path = Path
        };
        response.Cookies.Append(Name, string.Empty, expired);
    }

    /// <summary>
    /// Reads the refresh token, preferring an explicit body value (legacy clients) and falling
    /// back to the cookie. Returns null when neither is present.
    /// </summary>
    public static string? Read(HttpRequest request, string? fromBody)
        => !string.IsNullOrWhiteSpace(fromBody)
            ? fromBody
            : request.Cookies.TryGetValue(Name, out var cookie) && !string.IsNullOrWhiteSpace(cookie)
                ? cookie
                : null;

    /// <summary>
    /// CSRF check for the cookie-borne case: the caller must have sent <see cref="CsrfHeaderName"/>.
    /// A body-supplied token needs no check — whoever set the body already had script execution on
    /// an allowed origin.
    /// </summary>
    public static bool CsrfSatisfied(HttpRequest request, string? fromBody)
        => !string.IsNullOrWhiteSpace(fromBody)
           || !string.IsNullOrWhiteSpace(request.Headers[CsrfHeaderName].ToString());
}
