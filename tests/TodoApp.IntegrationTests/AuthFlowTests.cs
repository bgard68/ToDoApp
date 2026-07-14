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
        var client = _factory.CreateClient();

        var auth = await client.LoginAsync("demo@todoapp.local", "Password123!");

        auth.AccessToken.Should().NotBeNullOrEmpty();
        auth.User.Email.Should().Be("demo@todoapp.local");
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
