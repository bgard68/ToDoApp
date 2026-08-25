using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using TodoApp.WebApi.Authentication;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// The cookie helper decides where a refresh token may come from and whether the CSRF proof is
/// present. Both are security decisions, so every input shape is pinned down here.
/// </summary>
public class RefreshTokenCookieHelperTests
{
    private static HttpRequest RequestWith(string? cookieValue = null, string? csrfHeader = null)
    {
        var context = new DefaultHttpContext();

        if (cookieValue is not null)
        {
            SetCookies(context, RefreshTokenCookie.Name, cookieValue);
        }

        if (csrfHeader is not null)
        {
            context.Request.Headers[RefreshTokenCookie.CsrfHeaderName] = csrfHeader;
        }

        return context.Request;
    }

    // Installed as a feature rather than assigned to Request.Cookies: that setter re-serializes
    // into a Cookie header and rejects a blank or whitespace value, which would make the
    // "cookie present but blank" case — a real thing a browser can send — impossible to test.
    private static void SetCookies(HttpContext context, string name, string value) =>
        context.Features.Set<IRequestCookiesFeature>(new StubCookiesFeature(new StubCookies(name, value)));

    private sealed class StubCookiesFeature : IRequestCookiesFeature
    {
        public StubCookiesFeature(IRequestCookieCollection cookies) => Cookies = cookies;

        public IRequestCookieCollection Cookies { get; set; }
    }

    private sealed class StubCookies : IRequestCookieCollection
    {
        private readonly Dictionary<string, string> _cookies;

        public StubCookies(string name, string value) =>
            _cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [name] = value
            };

        public string? this[string key] => _cookies.TryGetValue(key, out var v) ? v : null;

        public int Count => _cookies.Count;

        public ICollection<string> Keys => _cookies.Keys;

        public bool ContainsKey(string key) => _cookies.ContainsKey(key);

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value)
            => _cookies.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _cookies.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Read_PrefersTheBodyValue()
    {
        RefreshTokenCookie.Read(RequestWith(cookieValue: "from-cookie"), "from-body")
            .Should().Be("from-body");
    }

    [Fact]
    public void Read_FallsBackToTheCookie()
    {
        RefreshTokenCookie.Read(RequestWith(cookieValue: "from-cookie"), fromBody: null)
            .Should().Be("from-cookie");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_TreatsABlankBodyValueAsAbsent(string fromBody)
    {
        RefreshTokenCookie.Read(RequestWith(cookieValue: "from-cookie"), fromBody)
            .Should().Be("from-cookie");
    }

    [Fact]
    public void Read_WithNoCookieAndNoBody_IsNull()
    {
        RefreshTokenCookie.Read(RequestWith(), fromBody: null).Should().BeNull();
    }

    [Fact]
    public void Read_WithADifferentCookieButNotOurs_IsNull()
    {
        var context = new DefaultHttpContext();
        SetCookies(context, "some_other_cookie", "value");

        RefreshTokenCookie.Read(context.Request, fromBody: null).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_TreatsABlankCookieAsAbsent(string cookieValue)
    {
        RefreshTokenCookie.Read(RequestWith(cookieValue: cookieValue), fromBody: null)
            .Should().BeNull();
    }

    [Fact]
    public void Csrf_IsSatisfiedByABodySuppliedToken()
    {
        // Whoever set the body already had script execution on an allowed origin.
        RefreshTokenCookie.CsrfSatisfied(RequestWith(), "from-body").Should().BeTrue();
    }

    [Fact]
    public void Csrf_IsSatisfiedByThePresenceOfTheHeader()
    {
        // The header's presence is the proof, not its value: a cross-site form post cannot set it.
        RefreshTokenCookie.CsrfSatisfied(RequestWith(csrfHeader: "1"), fromBody: null)
            .Should().BeTrue();
    }

    [Fact]
    public void Csrf_IsNotSatisfiedByACookieAlone()
    {
        RefreshTokenCookie.CsrfSatisfied(RequestWith(cookieValue: "from-cookie"), fromBody: null)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Csrf_IsNotSatisfiedByABlankHeader(string header)
    {
        RefreshTokenCookie.CsrfSatisfied(RequestWith(csrfHeader: header), fromBody: null)
            .Should().BeFalse();
    }

    [Fact]
    public void Write_SetsAnHttpOnlyCrossSiteCookieScopedToTheAuthEndpoints()
    {
        var context = new DefaultHttpContext();
        var expiry = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

        RefreshTokenCookie.Write(context.Response, "the-token", expiry);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain($"{RefreshTokenCookie.Name}=the-token");
        setCookie.Should().Contain("httponly");
        setCookie.Should().Contain("secure");
        setCookie.Should().Contain("samesite=none");
        setCookie.Should().Contain("path=/api/auth");
    }

    [Fact]
    public void Clear_ExpiresTheCookie()
    {
        var context = new DefaultHttpContext();

        RefreshTokenCookie.Clear(context.Response);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        setCookie.Should().Contain($"{RefreshTokenCookie.Name}=");
        setCookie.Should().Contain("expires=Thu, 01 Jan 1970");
    }
}
