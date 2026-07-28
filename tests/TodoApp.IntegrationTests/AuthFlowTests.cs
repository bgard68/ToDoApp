using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace TodoApp.IntegrationTests;

public class AuthFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public AuthFlowTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Todos_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/todos");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_ThenAccessTodos_Succeeds()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        var response = await client.GetAsync("/api/todos");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var todos = await response.Content.ReadFromJsonAsync<List<TodoResult>>();
        todos.Should().BeEmpty(); // brand-new user has no todos
    }

    [Fact]
    public async Task Login_WithSeededDemoUser_Succeeds()
    {
        // Seeding is opt-in since review finding H1, so this test now stands up a host that asks
        // for it — and supplies the password, rather than relying on a constant in the assembly.
        using var factory = new SeededDemoFactory();
        var client = factory.CreateClient();

        var auth = await client.LoginAsync(SeededDemoFactory.Email, SeededDemoFactory.Password);

        auth.AccessToken.Should().NotBeNullOrEmpty();
        auth.User.Email.Should().Be(SeededDemoFactory.Email);
    }

    /// <summary>A host with demo seeding explicitly enabled (the opt-in path from H1).</summary>
    private sealed class SeededDemoFactory : CustomWebApplicationFactory
    {
        public const string Email = "demo@todoapp.local";
        public const string Password = "SeededForTests1!";

        protected override IEnumerable<KeyValuePair<string, string?>> TestConfiguration =>
        [
            new("RateLimiting:Auth:PermitLimit", "10000"),
            new("RateLimiting:Global:PermitLimit", "10000"),
            new("Seed:DemoUser", "true"),
            new("Seed:Email", Email),
            new("Seed:Password", Password),
        ];
    }

    [Fact]
    public async Task RevokeAll_InvalidatesExistingAccessTokenImmediately()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        // Token works before revocation.
        (await client.GetAsync("/api/todos")).StatusCode.Should().Be(HttpStatusCode.OK);

        var revoke = await client.PostAsJsonAsync("/api/auth/revoke-all", new { });
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The SAME access token is now rejected because the security stamp rotated.
        (await client.GetAsync("/api/todos")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_RotatesToken_AndReuseIsRejected()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();

        var first = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = auth.RefreshToken });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await first.Content.ReadFromJsonAsync<AuthResult>();
        rotated!.RefreshToken.Should().NotBe(auth.RefreshToken);

        // Replaying the original (now-rotated) refresh token is detected and refused.
        var reuse = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = auth.RefreshToken });
        reuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
