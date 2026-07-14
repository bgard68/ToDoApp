using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace TodoApp.IntegrationTests;

internal static class ApiHelpers
{
    public static string UniqueEmail() => $"user-{Guid.NewGuid():N}@example.com";

    public static async Task<AuthResult> RegisterAsync(
        this HttpClient client, string? email = null, string password = "Password1")
    {
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new { email = email ?? UniqueEmail(), password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResult>())!;
    }

    public static async Task<AuthResult> LoginAsync(this HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResult>())!;
    }

    public static void Authorize(this HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
}
