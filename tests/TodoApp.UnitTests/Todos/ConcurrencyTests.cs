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

    private (int userId, TodoItem todo) Seed(TestDatabase db)
    {
        var user = new User("c@x.com", "hash", _clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();

        var todo = new TodoItem(user.Id, "Original", null, Priority.Low, null, null, _clock.UtcNow);
        db.Context.TodoItems.Add(todo);
        db.Context.SaveChanges();
        return (user.Id, todo);
    }

    private UpdateTodoCommandHandler Handler(TestDatabase db, int userId) =>
        new(db.NewContext(), new FakeCurrentUserService { UserId = userId }, _clock);

    [Fact]
    public async Task Update_WithStaleToken_ThrowsConcurrencyConflictWithCurrentValue()
    {
        using var db = new TestDatabase();
        var (userId, todo) = Seed(db);

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
        var (userId, todo) = Seed(db);

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
        var (userId, todo) = Seed(db);

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
