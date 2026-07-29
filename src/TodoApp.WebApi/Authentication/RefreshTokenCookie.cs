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
    /// Double-submit companion: a non-httpOnly cookie holding a random value that the client must
    /// echo in a header. A cross-site attacker can cause the httpOnly cookie to be sent, but
    /// cannot read this one to reproduce the header (that is what the same-origin policy blocks).
    /// </summary>
    public const string CsrfCookieName = "todo_rt_csrf";
    public const string CsrfHeaderName = "X-Refresh-CSRF";

    /// <summary>Scope the cookie to the auth endpoints — nothing else needs to receive it.</summary>
    private const string Path = "/api/auth";

    public static void Write(HttpResponse response, string refreshToken, DateTimeOffset expiresAt, string csrfValue)
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

        response.Cookies.Append(CsrfCookieName, csrfValue, new CookieOptions
        {
            HttpOnly = false,                   // the client must read it to echo it back
            Secure = true,
            SameSite = SameSiteMode.None,
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
        response.Cookies.Append(CsrfCookieName, string.Empty, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.None,
            Expires = DateTimeOffset.UnixEpoch,
            Path = Path
        });
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
    /// Double-submit check: when the token came from the cookie, the caller must also echo the
    /// CSRF cookie's value in a header. A body-supplied token needs no check — an attacker who
    /// can set the body already has script execution on an allowed origin.
    /// </summary>
    public static bool CsrfSatisfied(HttpRequest request, string? fromBody)
    {
        if (!string.IsNullOrWhiteSpace(fromBody))
        {
            return true;
        }

        if (!request.Cookies.TryGetValue(CsrfCookieName, out var cookie) || string.IsNullOrWhiteSpace(cookie))
        {
            return false;
        }

        var header = request.Headers[CsrfHeaderName].ToString();
        return !string.IsNullOrWhiteSpace(header)
            && CryptographicEquals(header, cookie);
    }

    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }
}
