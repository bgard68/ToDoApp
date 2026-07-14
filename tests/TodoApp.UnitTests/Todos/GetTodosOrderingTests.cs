using FluentAssertions;
using TodoApp.Application.Todos.Queries.GetTodos;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Todos;

/// <summary>
/// Verifies that ordering todos by DateTimeOffset columns works against real SQLite — which
/// only succeeds because DateTimeOffset is stored as a UTC-tick long (the converter). Without
/// it, the ORDER BY on DueDate/CreatedAt throws NotSupportedException at query translation.
/// </summary>
public class GetTodosOrderingTests
{
    private readonly FakeDateTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task GetTodos_OrdersByPriorityThenDueDate_WithoutThrowing()
    {
        using var db = new TestDatabase();
        var user = new User("o@x.com", "hash", _clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();

        var now = _clock.UtcNow;
        var done = new TodoItem(user.Id, "done", null, Priority.High, null, null, now);
        done.MoveTo(TodoStatus.Done, now);

        db.Context.TodoItems.AddRange(
            new TodoItem(user.Id, "high-due-later", null, Priority.High, null, now.AddDays(5), now),
            new TodoItem(user.Id, "high-due-soon", null, Priority.High, null, now.AddDays(1), now),
            new TodoItem(user.Id, "low-no-due", null, Priority.Low, null, null, now),
            done);
        db.Context.SaveChanges();

        var handler = new GetTodosQueryHandler(db.NewContext(), new FakeCurrentUserService { UserId = user.Id });
        var result = await handler.Handle(new GetTodosQuery(), CancellationToken.None);

        // High priority before Low; within priority, earliest due date first.
        result.Select(t => t.Title).Should()
            .ContainInOrder("high-due-soon", "high-due-later", "low-no-due");
    }

    [Fact]
    public async Task DateTimeOffset_RoundTripsThroughSqliteLosslessly()
    {
        using var db = new TestDatabase();
        var user = new User("r@x.com", "hash", _clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();

        var when = new DateTimeOffset(2026, 3, 15, 8, 30, 45, 123, TimeSpan.Zero);
        db.Context.TodoItems.Add(new TodoItem(user.Id, "x", null, Priority.Low, null, when, when));
        db.Context.SaveChanges();

        using var read = db.NewContext();
        var loaded = read.TodoItems.Single();

        loaded.CreatedAt.Should().Be(when);
        loaded.DueDate.Should().Be(when);
    }
}
