using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// Review finding H2 (second half) — the refresh token is delivered as an httpOnly cookie so an
/// XSS cannot read it, instead of a JSON field the SPA parks in localStorage.
/// </summary>
public class RefreshCookieTests : IClassFixture<CookieOnlyFactory>
{
    private readonly CookieOnlyFactory _factory;

    public RefreshCookieTests(CookieOnlyFactory factory) => _factory = factory;


    /// <summary>
    /// The refresh cookie is Secure, so a cookie container will not store or send it over plain
    /// http. The TestServer does no real TLS, but the handler decides from the URI scheme — so the
    /// client must speak https for the cookie round-trip to behave like a browser's.
    /// </summary>
    private HttpClient HttpsClient(bool handleCookies = true) =>
        _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = handleCookies
        });

    private static string? CookieAttribute(HttpResponseMessage response, string cookieName)
        => response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(cookieName + "=", StringComparison.Ordinal))
            : null;

    [Fact]
    public async Task Login_sets_an_httpOnly_secure_refresh_cookie()
    {
        var client = HttpsClient(handleCookies: false);

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { email = ApiHelpers.UniqueEmail(), password = "Password1" });
        register.EnsureSuccessStatusCode();

        var setCookie = CookieAttribute(register, "todo_rt");
        setCookie.Should().NotBeNull("the refresh token must be delivered as a cookie");
        setCookie!.Should().Contain("httponly", Exactly.Once(), because: "JavaScript must not be able to read it");
        setCookie.Should().Contain("secure", Exactly.Once());
        setCookie.Should().Contain("samesite=none", Exactly.Once(),
            "the SPA and API are different sites, so Lax/Strict would never send it");
        setCookie.Should().Contain("path=/api/auth", Exactly.Once(),
            "nothing outside the auth endpoints needs to receive it");
    }

    [Fact]
    public async Task The_refresh_token_is_not_returned_in_the_response_body()
    {
        var client = HttpsClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = ApiHelpers.UniqueEmail(), password = "Password1" });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<AuthResult>();
        body!.RefreshToken.Should().BeNullOrEmpty(
            "H2 — with cookie delivery on, the SPA has nothing to put in localStorage");
        body.AccessToken.Should().NotBeNullOrEmpty("the access token still comes back for the Authorization header");
    }

    [Fact]
    public async Task Refresh_works_from_the_cookie_alone_with_the_csrf_header()
    {
        var client = HttpsClient();

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { email = ApiHelpers.UniqueEmail(), password = "Password1" });
        register.EnsureSuccessStatusCode();

        var csrf = ExtractCsrf(register);
        csrf.Should().NotBeNullOrEmpty();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("X-Refresh-CSRF", csrf);

        var refreshed = await client.SendAsync(request);

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK,
            "the cookie carries the token; no body value is needed");
    }

    [Fact]
    public async Task Refresh_is_rejected_without_the_csrf_header()
    {
        var client = HttpsClient();

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { email = ApiHelpers.UniqueEmail(), password = "Password1" });
        register.EnsureSuccessStatusCode();

        // Cookie is present (the handler stores it) but the double-submit header is missing —
        // exactly the shape of a cross-site forgery attempt.
        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new { });

        refreshed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_is_rejected_when_the_csrf_header_does_not_match_the_cookie()
    {
        var client = HttpsClient();

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { email = ApiHelpers.UniqueEmail(), password = "Password1" });
        register.EnsureSuccessStatusCode();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { })
        };
        request.Headers.Add("X-Refresh-CSRF", "0000000000000000000000000000000000");

        var refreshed = await client.SendAsync(request);

        refreshed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_clears_the_cookie()
    {
        var client = HttpsClient();

        var register = await client.PostAsJsonAsync("/api/auth/register",
            new { email = ApiHelpers.UniqueEmail(), password = "Password1" });
        register.EnsureSuccessStatusCode();
        var auth = await register.Content.ReadFromJsonAsync<AuthResult>();
        client.Authorize(auth!.AccessToken);

        var logout = await client.PostAsJsonAsync("/api/auth/logout", new { });

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cleared = CookieAttribute(logout, "todo_rt");
        cleared.Should().NotBeNull();
        cleared!.Should().Contain("expires=", Exactly.Once(), "the cookie must be actively expired");
    }

    private static string? ExtractCsrf(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault(v => v.StartsWith("todo_rt_csrf=", StringComparison.Ordinal));
        if (raw is null)
        {
            return null;
        }

        var value = raw["todo_rt_csrf=".Length..];
        var end = value.IndexOf(';');
        return end >= 0 ? value[..end] : value;
    }
}

/// <summary>A host with cookie-only refresh delivery — the deployed default.</summary>
public sealed class CookieOnlyFactory : CustomWebApplicationFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> TestConfiguration =>
    [
        new("RateLimiting:Auth:PermitLimit", "10000"),
        new("RateLimiting:Global:PermitLimit", "10000"),
        new("Seed:DemoUser", "false"),
        new("Auth:RefreshTokenInBody", "false"),
        new("PasswordBreachCheck:Enabled", "false"),
    ];
}
