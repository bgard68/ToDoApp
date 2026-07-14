using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace TodoApp.IntegrationTests;

public class CategoryApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public CategoryApiTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_SeedsDefaultCategories()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        var categories = await client.GetFromJsonAsync<List<CategoryResult>>("/api/categories");

        categories.Should().NotBeNull();
        categories!.Select(c => c.Name).Should()
            .Contain(new[] { "Work", "Personal", "Errands", "Study", "Other" });
    }

    [Fact]
    public async Task Create_ThenList_ReturnsTheCategory()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        var create = await client.PostAsJsonAsync("/api/categories",
            new { name = "Fitness", color = "#22c55e" });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<CategoryResult>();
        created!.Name.Should().Be("Fitness");
        created.Color.Should().Be("#22c55e");

        var categories = await client.GetFromJsonAsync<List<CategoryResult>>("/api/categories");
        categories.Should().ContainSingle(c => c.Id == created.Id && c.Name == "Fitness");
    }

    [Fact]
    public async Task Create_WithDuplicateName_Returns409()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        (await client.PostAsJsonAsync("/api/categories", new { name = "Travel", color = "#3b82f6" }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var duplicate = await client.PostAsJsonAsync("/api/categories",
            new { name = "Travel", color = "#f97316" });

        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_WithInvalidColor_Returns400()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        var res = await client.PostAsJsonAsync("/api/categories",
            new { name = "Bad", color = "not-a-color" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeletingCategory_LeavesItsTasksUncategorized()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();
        client.Authorize(auth.AccessToken);

        var createCat = await client.PostAsJsonAsync("/api/categories",
            new { name = "Chores", color = "#a855f7" });
        var category = await createCat.Content.ReadFromJsonAsync<CategoryResult>();

        var createTodo = await client.PostAsJsonAsync("/api/todos",
            new { title = "Vacuum", priority = 1, categoryId = category!.Id });
        var todo = await createTodo.Content.ReadFromJsonAsync<TodoResult>();
        todo!.CategoryId.Should().Be(category.Id);

        var delete = await client.DeleteAsync($"/api/categories/{category.Id}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refreshed = await client.GetFromJsonAsync<TodoResult>($"/api/todos/{todo.Id}");
        refreshed!.CategoryId.Should().BeNull();
    }

    [Fact]
    public async Task User_CannotSeeOrModifyAnotherUsersCategory()
    {
        var clientA = _factory.CreateClient();
        var authA = await clientA.RegisterAsync();
        clientA.Authorize(authA.AccessToken);
        var createA = await clientA.PostAsJsonAsync("/api/categories",
            new { name = "A-only", color = "#ef4444" });
        var catA = await createA.Content.ReadFromJsonAsync<CategoryResult>();

        var clientB = _factory.CreateClient();
        var authB = await clientB.RegisterAsync();
        clientB.Authorize(authB.AccessToken);

        var listB = await clientB.GetFromJsonAsync<List<CategoryResult>>("/api/categories");
        listB.Should().NotContain(c => c.Id == catA!.Id);

        var updateB = await clientB.PutAsJsonAsync($"/api/categories/{catA!.Id}",
            new { name = "hijacked", color = "#000000" });
        updateB.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var deleteB = await clientB.DeleteAsync($"/api/categories/{catA.Id}");
        deleteB.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreatingTodo_WithAnotherUsersCategory_Returns404()
    {
        var clientA = _factory.CreateClient();
        var authA = await clientA.RegisterAsync();
        clientA.Authorize(authA.AccessToken);
        var createA = await clientA.PostAsJsonAsync("/api/categories",
            new { name = "A-secret", color = "#14b8a6" });
        var catA = await createA.Content.ReadFromJsonAsync<CategoryResult>();

        var clientB = _factory.CreateClient();
        var authB = await clientB.RegisterAsync();
        clientB.Authorize(authB.AccessToken);

        var res = await clientB.PostAsJsonAsync("/api/todos",
            new { title = "Sneaky", priority = 1, categoryId = catA!.Id });

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
