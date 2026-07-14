using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace TodoApp.IntegrationTests;

public class TodoApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TodoApiTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_ThenList_ReturnsTheTodo()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        var create = await client.PostAsJsonAsync("/api/todos", new { title = "Buy milk", priority = 2 });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<TodoResult>();
        created!.Title.Should().Be("Buy milk");

        var list = await client.GetFromJsonAsync<List<TodoResult>>("/api/todos");
        list.Should().ContainSingle(t => t.Id == created.Id && t.Title == "Buy milk");
    }

    [Fact]
    public async Task User_CannotAccessAnotherUsersTodo()
    {
        // User A creates a todo.
        var clientA = _factory.CreateClient();
        var authA = await clientA.RegisterAsync();
        clientA.Authorize(authA.AccessToken);
        var create = await clientA.PostAsJsonAsync("/api/todos", new { title = "A's secret", priority = 1 });
        var created = await create.Content.ReadFromJsonAsync<TodoResult>();

        // User B must not see or touch it.
        var clientB = _factory.CreateClient();
        var authB = await clientB.RegisterAsync();
        clientB.Authorize(authB.AccessToken);

        var list = await clientB.GetFromJsonAsync<List<TodoResult>>("/api/todos");
        list.Should().BeEmpty();

        (await clientB.GetAsync($"/api/todos/{created!.Id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await clientB.DeleteAsync($"/api/todos/{created.Id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        // And A's todo is still there.
        var listA = await clientA.GetFromJsonAsync<List<TodoResult>>("/api/todos");
        listA.Should().ContainSingle(t => t.Id == created.Id);
    }

    [Fact]
    public async Task Update_WithStaleConcurrencyToken_Returns409()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        var create = await client.PostAsJsonAsync("/api/todos", new { title = "Original", priority = 1 });
        var created = await create.Content.ReadFromJsonAsync<TodoResult>();

        var res = await client.PutAsJsonAsync($"/api/todos/{created!.Id}", new
        {
            title = "Changed",
            priority = 2,
            concurrencyToken = Guid.NewGuid() // stale
        });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Update_WithCurrentConcurrencyToken_Succeeds()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        var create = await client.PostAsJsonAsync("/api/todos", new { title = "Original", priority = 1 });
        var created = await create.Content.ReadFromJsonAsync<TodoResult>();

        var res = await client.PutAsJsonAsync($"/api/todos/{created!.Id}", new
        {
            title = "Changed",
            priority = 2,
            concurrencyToken = created.ConcurrencyToken // current
        });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await res.Content.ReadFromJsonAsync<TodoResult>();
        updated!.Title.Should().Be("Changed");
        updated.ConcurrencyToken.Should().NotBe(created.ConcurrencyToken); // token rotated on change
    }

    [Fact]
    public async Task ChangeStatus_MovesTaskToDoneLane()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        var create = await client.PostAsJsonAsync("/api/todos", new { title = "Ship it", priority = 1 });
        var created = await create.Content.ReadFromJsonAsync<TodoResult>();
        created!.Status.Should().Be(0);          // starts in To Do
        created.IsCompleted.Should().BeFalse();

        var move = await client.PatchAsJsonAsync($"/api/todos/{created.Id}/status", new { status = 2 }); // Done
        move.StatusCode.Should().Be(HttpStatusCode.OK);

        var moved = await move.Content.ReadFromJsonAsync<TodoResult>();
        moved!.Status.Should().Be(2);
        moved.IsCompleted.Should().BeTrue();      // Done lane -> completed
    }
}
