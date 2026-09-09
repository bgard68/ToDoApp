using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// Functional and integration coverage for the /api/todos surface, driven through the real
/// HTTP pipeline (routing, model binding, auth, MediatR, EF Core, SQLite) with nothing mocked.
/// Complements the handler-level unit tests, which cannot see routing, status-code mapping,
/// serialization or the auth filter.
/// </summary>
public class TodoLifecycleIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TodoLifecycleIntegrationTests(CustomWebApplicationFactory factory) => _factory = factory;

    /// <summary>A client authenticated as a brand-new user, isolated from every other test.</summary>
    private async Task<HttpClient> NewSignedInClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);
        return client;
    }

    private static async Task<TodoResult> CreateTodoAsync(HttpClient client, string title = "Write the report")
    {
        var response = await client.PostAsJsonAsync("/api/todos",
            new { title, description = "  padded  ", priority = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<TodoResult>())!;
    }

    // ---- Functional: the full board workflow ---------------------------------------

    [Fact]
    public async Task TodoLifecycle_CreateMoveCompleteDelete_WalksTheBoardAndLeavesNoRow()
    {
        var client = await NewSignedInClientAsync();

        var created = await CreateTodoAsync(client);
        var toInProgress = await client.PatchAsJsonAsync($"/api/todos/{created.Id}/status", new { status = 1 });
        var toDone = await client.PatchAsJsonAsync($"/api/todos/{created.Id}/status", new { status = 2 });
        var deleted = await client.DeleteAsync($"/api/todos/{created.Id}");
        var afterDelete = await client.GetAsync($"/api/todos/{created.Id}");

        created.Title.Should().Be("Write the report");
        created.Description.Should().Be("padded");
        created.Status.Should().Be(0);
        created.IsCompleted.Should().BeFalse();

        (await toInProgress.Content.ReadFromJsonAsync<TodoResult>())!.Status.Should().Be(1);

        var done = (await toDone.Content.ReadFromJsonAsync<TodoResult>())!;
        done.Status.Should().Be(2);
        done.IsCompleted.Should().BeTrue();
        done.StatusName.Should().Be("Done");

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        afterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_ThenList_ReturnsOnlyTheCallersOwnTasks()
    {
        var mine = await NewSignedInClientAsync();
        var theirs = await NewSignedInClientAsync();
        await CreateTodoAsync(mine, "Mine");
        await CreateTodoAsync(theirs, "Theirs");

        var listed = await mine.GetFromJsonAsync<List<TodoResult>>("/api/todos");

        listed.Should().ContainSingle();
        listed![0].Title.Should().Be("Mine");
    }

    // ---- Integration: cross-tenant isolation ----------------------------------------

    [Fact]
    public async Task GetById_ForAnotherUsersTask_Returns404NotAndNot403()
    {
        var owner = await NewSignedInClientAsync();
        var intruder = await NewSignedInClientAsync();
        var theirs = await CreateTodoAsync(owner, "Confidential");

        var response = await intruder.GetAsync($"/api/todos/{theirs.Id}");

        // 404 rather than 403: a 403 would confirm the row exists, which is itself a disclosure.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_OfAnotherUsersTask_Returns404AndLeavesTheRowIntact()
    {
        var owner = await NewSignedInClientAsync();
        var intruder = await NewSignedInClientAsync();
        var theirs = await CreateTodoAsync(owner, "Survives");

        var attack = await intruder.DeleteAsync($"/api/todos/{theirs.Id}");
        var ownerView = await owner.GetFromJsonAsync<TodoResult>($"/api/todos/{theirs.Id}");

        attack.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ownerView!.Title.Should().Be("Survives");
    }

    [Fact]
    public async Task ChangeStatus_OnAnotherUsersTask_Returns404AndDoesNotMoveTheCard()
    {
        var owner = await NewSignedInClientAsync();
        var intruder = await NewSignedInClientAsync();
        var theirs = await CreateTodoAsync(owner, "Stays in ToDo");

        var attack = await intruder.PatchAsJsonAsync($"/api/todos/{theirs.Id}/status", new { status = 2 });
        var ownerView = await owner.GetFromJsonAsync<TodoResult>($"/api/todos/{theirs.Id}");

        attack.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ownerView!.Status.Should().Be(0);
        ownerView.IsCompleted.Should().BeFalse();
    }

    // ---- Integration: optimistic concurrency ----------------------------------------

    [Fact]
    public async Task Update_WithStaleConcurrencyToken_Returns409AndKeepsTheWinningEdit()
    {
        var client = await NewSignedInClientAsync();
        var created = await CreateTodoAsync(client, "Original");
        var staleToken = created.ConcurrencyToken;

        var firstWrite = await client.PutAsJsonAsync($"/api/todos/{created.Id}",
            new { title = "First writer wins", priority = 1, concurrencyToken = staleToken });
        var secondWrite = await client.PutAsJsonAsync($"/api/todos/{created.Id}",
            new { title = "Second writer loses", priority = 1, concurrencyToken = staleToken });
        var current = await client.GetFromJsonAsync<TodoResult>($"/api/todos/{created.Id}");

        firstWrite.StatusCode.Should().Be(HttpStatusCode.OK);
        secondWrite.StatusCode.Should().Be(HttpStatusCode.Conflict);
        current!.Title.Should().Be("First writer wins");
    }

    [Fact]
    public async Task Update_RotatesTheConcurrencyTokenOnEverySuccessfulWrite()
    {
        var client = await NewSignedInClientAsync();
        var created = await CreateTodoAsync(client, "Original");

        var response = await client.PutAsJsonAsync($"/api/todos/{created.Id}",
            new { title = "Edited", priority = 1, concurrencyToken = created.ConcurrencyToken });
        var updated = (await response.Content.ReadFromJsonAsync<TodoResult>())!;

        updated.ConcurrencyToken.Should().NotBe(created.ConcurrencyToken);
        updated.ConcurrencyToken.Should().NotBe(Guid.Empty);
    }

    // ---- Integration: authentication boundary ----------------------------------------

    [Fact]
    public async Task ListTodos_WithoutAToken_Returns401()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/todos");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateTodo_WithAMalformedBearerToken_Returns401AndPersistsNothing()
    {
        var anonymous = _factory.CreateClient();
        anonymous.Authorize("not-a-real-jwt");
        var owner = await NewSignedInClientAsync();

        var response = await anonymous.PostAsJsonAsync("/api/todos", new { title = "Injected", priority = 1 });
        var ownersBoard = await owner.GetFromJsonAsync<List<TodoResult>>("/api/todos");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ownersBoard.Should().BeEmpty();
    }

    // ---- Integration: validation at the HTTP boundary --------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateTodo_WithBlankTitle_Returns400AndCreatesNothing(string title)
    {
        var client = await NewSignedInClientAsync();

        var response = await client.PostAsJsonAsync("/api/todos", new { title, priority = 1 });
        var board = await client.GetFromJsonAsync<List<TodoResult>>("/api/todos");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        board.Should().BeEmpty();
    }

    [Fact]
    public async Task GetById_ForAnIdThatNeverExisted_Returns404()
    {
        var client = await NewSignedInClientAsync();

        var response = await client.GetAsync("/api/todos/999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChangeStatus_ToAnUndefinedLane_Returns400AndLeavesTheCardPut()
    {
        var client = await NewSignedInClientAsync();
        var created = await CreateTodoAsync(client, "Stays put");

        var response = await client.PatchAsJsonAsync($"/api/todos/{created.Id}/status", new { status = 99 });
        var current = await client.GetFromJsonAsync<TodoResult>($"/api/todos/{created.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        current!.Status.Should().Be(0);
    }
}
