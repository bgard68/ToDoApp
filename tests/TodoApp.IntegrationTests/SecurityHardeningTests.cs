using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TodoApp.Infrastructure.Persistence;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// End-to-end guards for the review findings that are only observable through the HTTP pipeline:
/// H1 (no implicit demo account), H2 (response security headers), H3 (auth rate limiting).
/// </summary>
public class SecurityHardeningTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SecurityHardeningTests(CustomWebApplicationFactory factory) => _factory = factory;

    // ---- H1: the demo account must not exist unless configured ----------------------------

    [Fact]
    public async Task Demo_account_is_not_seeded_by_default()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var demo = await db.Users.FirstOrDefaultAsync(u => u.Email == "demo@todoapp.local");

        demo.Should().BeNull("H1 — seeding must be opt-in, not a side effect of startup");
    }

    [Fact]
    public async Task The_old_hard_coded_demo_credentials_do_not_authenticate()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "demo@todoapp.local", password = "Password123!" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- H2: baseline response headers ----------------------------------------------------

    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "strict-origin-when-cross-origin")]
    public async Task Responses_carry_baseline_security_headers(string header, string expected)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/todos");   // 401, headers still apply

        response.Headers.TryGetValues(header, out var values).Should().BeTrue($"{header} must be set");
        values!.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public async Task Api_responses_carry_a_locked_down_content_security_policy()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/todos");

        response.Headers.GetValues("Content-Security-Policy").Should()
            .ContainSingle().Which.Should().Contain("default-src 'none'")
            .And.Contain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Security_headers_are_present_on_error_responses_too()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/todos/not-an-int");

        response.IsSuccessStatusCode.Should().BeFalse();
        response.Headers.Contains("X-Content-Type-Options").Should().BeTrue();
    }

    // ---- H3: auth endpoints are throttled --------------------------------------------------

    [Fact]
    public async Task Auth_endpoints_return_429_once_the_window_is_exhausted()
    {
        using var factory = new ThrottledFactory();
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < ThrottledFactory.PermitLimit + 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login",
                new { email = $"nobody-{i}@example.test", password = "WrongPassword1" });
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "H3 — brute forcing /api/auth/login must be throttled, not merely rejected");

        statuses.Take(ThrottledFactory.PermitLimit).Should()
            .NotContain(HttpStatusCode.TooManyRequests, "requests inside the window must still be served");
    }

    [Fact]
    public async Task A_throttled_response_tells_the_client_when_to_retry()
    {
        using var factory = new ThrottledFactory();
        var client = factory.CreateClient();

        HttpResponseMessage? throttled = null;
        for (var i = 0; i < ThrottledFactory.PermitLimit + 3 && throttled is null; i++)
        {
            var response = await client.PostAsJsonAsync("/api/auth/login",
                new { email = $"nobody-{i}@example.test", password = "WrongPassword1" });
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttled = response;
            }
        }

        throttled.Should().NotBeNull();
        throttled!.Headers.Contains("Retry-After").Should().BeTrue();
    }

    [Fact]
    public async Task An_oversized_password_is_rejected_as_a_validation_error()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "user@example.test", password = new string('a', 200_000) });

        // Rejected by validation (400) rather than absorbed by the hasher.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A host with a deliberately tiny auth window, so the limiter can be observed.</summary>
    private sealed class ThrottledFactory : CustomWebApplicationFactory
    {
        public const int PermitLimit = 5;

        protected override IEnumerable<KeyValuePair<string, string?>> TestConfiguration =>
        [
            new("RateLimiting:Auth:PermitLimit", PermitLimit.ToString()),
            new("RateLimiting:Auth:WindowSeconds", "60"),
            new("RateLimiting:Global:PermitLimit", "10000"),
            new("Seed:DemoUser", "false"),
        ];
    }
}
