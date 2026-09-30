using FluentAssertions;
using TodoApp.Application.Todos.Queries.GetTodos;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Todos;

/// <summary>
/// Verifies that ordering todos by DateTimeOffset columns works against real SQLite — which
/// only succeeds because DateTimeOffset is stored as a UTC-tick long (the Dapper type handler).
/// The round-trip test guards that the tick storage is lossless.
/// </summary>
public class GetTodosOrderingTests
{
    private readonly FakeDateTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task GetTodos_OrdersByPriorityThenDueDate_WithoutThrowing()
    {
        using var db = new TestDatabase();
        var user = new User("o@x.com", "hash", _clock.UtcNow);
        await db.Users.AddAsync(user, CancellationToken.None);

        var now = _clock.UtcNow;
        var done = new TodoItem(user.Id, "done", null, Priority.High, null, null, now);
        done.MoveTo(TodoStatus.Done, now);

        foreach (var item in new[]
        {
            new TodoItem(user.Id, "high-due-later", null, Priority.High, null, now.AddDays(5), now),
            new TodoItem(user.Id, "high-due-soon", null, Priority.High, null, now.AddDays(1), now),
            new TodoItem(user.Id, "low-no-due", null, Priority.Low, null, null, now),
            done
        })
        {
            await db.Todos.AddAsync(item, CancellationToken.None);
        }

        var handler = new GetTodosQueryHandler(db.Todos, new FakeCurrentUserService { UserId = user.Id });
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
        await db.Users.AddAsync(user, CancellationToken.None);

        var when = new DateTimeOffset(2026, 3, 15, 8, 30, 45, 123, TimeSpan.Zero);
        await db.Todos.AddAsync(new TodoItem(user.Id, "x", null, Priority.Low, null, when, when), CancellationToken.None);

        var loaded = (await db.Todos.GetForUserAsync(user.Id, TodoFilter.All, null, CancellationToken.None)).Single();

        loaded.CreatedAt.Should().Be(when);
        loaded.DueDate.Should().Be(when);
    }

    // Contract test for case-insensitive search. NOTE: SQLite's LIKE is already case-insensitive
    // for ASCII, so this passes on the test harness regardless — the LOWER() fix is what makes it
    // hold on Postgres (prod), whose LIKE is case-sensitive. Kept for parity with the EF branch and
    // to guard the intended behaviour.
    [Fact]
    public async Task GetTodos_SearchIsCaseInsensitive()
    {
        using var db = new TestDatabase();
        var user = new User("s@x.com", "hash", _clock.UtcNow);
        await db.Users.AddAsync(user, CancellationToken.None);

        var now = _clock.UtcNow;
        await db.Todos.AddAsync(
            new TodoItem(user.Id, "Email Sarah about the proposal", null, Priority.Medium, null, null, now),
            CancellationToken.None);
        await db.Todos.AddAsync(
            new TodoItem(user.Id, "Pay AWS invoice", null, Priority.Medium, null, null, now),
            CancellationToken.None);

        var handler = new GetTodosQueryHandler(db.Todos, new FakeCurrentUserService { UserId = user.Id });

        var lower = await handler.Handle(new GetTodosQuery { Search = "sarah" }, CancellationToken.None);
        lower.Select(t => t.Title).Should().BeEquivalentTo(["Email Sarah about the proposal"]);

        var upper = await handler.Handle(new GetTodosQuery { Search = "AWS" }, CancellationToken.None);
        upper.Select(t => t.Title).Should().BeEquivalentTo(["Pay AWS invoice"]);
    }
}
