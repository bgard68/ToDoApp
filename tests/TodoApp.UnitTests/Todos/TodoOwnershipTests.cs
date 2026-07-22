using FluentAssertions;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Todos.Commands.CreateTodo;
using TodoApp.Application.Todos.Commands.DeleteTodo;
using TodoApp.Application.Todos.Commands.UpdateTodo;
using TodoApp.Application.Todos.Queries.GetTodoById;
using TodoApp.Application.Todos.Queries.GetTodos;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Todos;

public class TodoOwnershipTests
{
    private readonly FakeDateTimeProvider _clock = new();

    private async Task<(int user1, int user2)> SeedTwoUsersAsync(TestDatabase db)
    {
        var u1 = new User("u1@x.com", "hash", _clock.UtcNow);
        var u2 = new User("u2@x.com", "hash", _clock.UtcNow);
        await db.Users.AddAsync(u1, CancellationToken.None);
        await db.Users.AddAsync(u2, CancellationToken.None);
        return (u1.Id, u2.Id);
    }

    [Fact]
    public async Task Create_AssignsTodoToCurrentUser()
    {
        using var db = new TestDatabase();
        var (user1, _) = await SeedTwoUsersAsync(db);
        var current = new FakeCurrentUserService { UserId = user1 };
        var handler = new CreateTodoCommandHandler(db.Todos, db.Categories, current, _clock);

        var result = await handler.Handle(new CreateTodoCommand { Title = "Mine" }, CancellationToken.None);

        result.Title.Should().Be("Mine");
        (await db.Todos.GetForUserAsync(user1, TodoFilter.All, null, CancellationToken.None))
            .Should().ContainSingle().Which.UserId.Should().Be(user1);
    }

    [Fact]
    public async Task GetTodos_ReturnsOnlyCurrentUsersItems()
    {
        using var db = new TestDatabase();
        var (user1, user2) = await SeedTwoUsersAsync(db);
        foreach (var item in new[]
        {
            new TodoItem(user1, "A1", null, Priority.Low, null, null, _clock.UtcNow),
            new TodoItem(user1, "A2", null, Priority.Low, null, null, _clock.UtcNow),
            new TodoItem(user2, "B1", null, Priority.Low, null, null, _clock.UtcNow)
        })
        {
            await db.Todos.AddAsync(item, CancellationToken.None);
        }

        var handler = new GetTodosQueryHandler(db.Todos, new FakeCurrentUserService { UserId = user1 });
        var result = await handler.Handle(new GetTodosQuery(), CancellationToken.None);

        result.Should().HaveCount(2);
        result.Select(t => t.Title).Should().BeEquivalentTo(new[] { "A1", "A2" });
    }

    [Fact]
    public async Task Update_AnotherUsersTodo_ThrowsNotFound()
    {
        using var db = new TestDatabase();
        var (user1, user2) = await SeedTwoUsersAsync(db);
        var todo = new TodoItem(user2, "B1", null, Priority.Low, null, null, _clock.UtcNow);
        await db.Todos.AddAsync(todo, CancellationToken.None);

        var handler = new UpdateTodoCommandHandler(db.Todos, db.Categories, new FakeCurrentUserService { UserId = user1 }, _clock);
        var act = () => handler.Handle(
            new UpdateTodoCommand { Id = todo.Id, Title = "Hijack", Priority = Priority.Low },
            CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Delete_AnotherUsersTodo_ThrowsNotFound()
    {
        using var db = new TestDatabase();
        var (user1, user2) = await SeedTwoUsersAsync(db);
        var todo = new TodoItem(user2, "B1", null, Priority.Low, null, null, _clock.UtcNow);
        await db.Todos.AddAsync(todo, CancellationToken.None);

        var handler = new DeleteTodoCommandHandler(db.Todos, new FakeCurrentUserService { UserId = user1 });
        var act = () => handler.Handle(new DeleteTodoCommand(todo.Id), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task GetById_AnotherUsersTodo_ThrowsNotFound()
    {
        using var db = new TestDatabase();
        var (user1, user2) = await SeedTwoUsersAsync(db);
        var todo = new TodoItem(user2, "B1", null, Priority.Low, null, null, _clock.UtcNow);
        await db.Todos.AddAsync(todo, CancellationToken.None);

        var handler = new GetTodoByIdQueryHandler(db.Todos, new FakeCurrentUserService { UserId = user1 });
        var act = () => handler.Handle(new GetTodoByIdQuery(todo.Id), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
