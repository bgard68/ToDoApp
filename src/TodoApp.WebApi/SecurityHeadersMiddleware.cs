namespace TodoApp.WebApi;

/// <summary>
/// Adds baseline response security headers to every API response.
/// </summary>
/// <remarks>
/// The API returns JSON rather than HTML, so these are defence in depth rather than the primary
/// control — the SPA's own headers do the heavy lifting (review finding H2). They still matter:
/// <c>nosniff</c> stops a browser from re-interpreting a JSON error body as HTML, and the
/// framing/referrer headers cover the Swagger UI, which <em>is</em> HTML.
/// </remarks>
public static class SecurityHeadersMiddleware
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";

            // API responses are data, never a document context — deny everything by default.
            // Swagger UI is real HTML with inline scripts and styles, so it gets a policy that
            // still forbids remote origins and framing but permits its own bundle to run.
            headers["Content-Security-Policy"] =
                context.Request.Path.StartsWithSegments("/swagger")
                    ? "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'"
                    : "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";

            await next();
        });
    }
}
