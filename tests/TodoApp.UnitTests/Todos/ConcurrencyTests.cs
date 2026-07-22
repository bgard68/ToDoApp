using FluentAssertions;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Todos.Commands.UpdateTodo;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Todos;

public class ConcurrencyTests
{
    private readonly FakeDateTimeProvider _clock = new();

    private async Task<(int userId, TodoItem todo)> SeedAsync(TestDatabase db)
    {
        var user = new User("c@x.com", "hash", _clock.UtcNow);
        await db.Users.AddAsync(user, CancellationToken.None);

        var todo = new TodoItem(user.Id, "Original", null, Priority.Low, null, null, _clock.UtcNow);
        await db.Todos.AddAsync(todo, CancellationToken.None);
        return (user.Id, todo);
    }

    private UpdateTodoCommandHandler Handler(TestDatabase db, int userId) =>
        new(db.Todos, db.Categories, new FakeCurrentUserService { UserId = userId }, _clock);

    [Fact]
    public async Task Update_WithStaleToken_ThrowsConcurrencyConflictWithCurrentValue()
    {
        using var db = new TestDatabase();
        var (userId, todo) = await SeedAsync(db);

        var act = () => Handler(db, userId).Handle(new UpdateTodoCommand
        {
            Id = todo.Id,
            Title = "Changed",
            Priority = Priority.High,
            ConcurrencyToken = Guid.NewGuid() // not the current token -> stale
        }, CancellationToken.None);

        var conflict = (await act.Should().ThrowAsync<ConcurrencyConflictException>()).Which;
        conflict.CurrentValue.Should().BeOfType<TodoItemDto>();
    }

    [Fact]
    public async Task Update_WithCurrentToken_Succeeds()
    {
        using var db = new TestDatabase();
        var (userId, todo) = await SeedAsync(db);

        var result = await Handler(db, userId).Handle(new UpdateTodoCommand
        {
            Id = todo.Id,
            Title = "Changed",
            Priority = Priority.High,
            ConcurrencyToken = todo.ConcurrencyToken // the value the client last saw
        }, CancellationToken.None);

        result.Title.Should().Be("Changed");
    }

    [Fact]
    public async Task Update_WithoutToken_Succeeds()
    {
        using var db = new TestDatabase();
        var (userId, todo) = await SeedAsync(db);

        var result = await Handler(db, userId).Handle(new UpdateTodoCommand
        {
            Id = todo.Id,
            Title = "Changed",
            Priority = Priority.High
            // no ConcurrencyToken -> concurrency not enforced (last-writer-wins)
        }, CancellationToken.None);

        result.Title.Should().Be("Changed");
    }
}
