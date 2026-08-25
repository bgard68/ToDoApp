using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Todos.Commands.ChangeStatus;
using TodoApp.Application.Todos.Commands.CreateTodo;
using TodoApp.Application.Todos.Commands.DeleteTodo;
using TodoApp.Application.Todos.Commands.UpdateTodo;
using TodoApp.Application.Todos.Dtos;
using TodoApp.Application.Todos.Queries.GetTodoById;
using TodoApp.Application.Todos.Queries.GetTodos;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Todos;

public class TodoHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new();

    private User SeedUser(TestDatabase db, string email = "todo@example.com")
    {
        var user = new User(email, "hash", _clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();
        return user;
    }

    private TodoItem SeedTodo(
        TestDatabase db,
        int userId,
        string title = "Task",
        string? description = null,
        Priority priority = Priority.Medium,
        DateTimeOffset? dueDate = null,
        TodoStatus status = TodoStatus.ToDo)
    {
        var todo = new TodoItem(userId, title, description, priority, null, dueDate, _clock.UtcNow);
        if (status != TodoStatus.ToDo)
        {
            todo.MoveTo(status, _clock.UtcNow);
        }

        db.Context.TodoItems.Add(todo);
        db.Context.SaveChanges();
        return todo;
    }

    // ---- Delete --------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheCallersOwnTask()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var todo = SeedTodo(db, user.Id);

        var handler = new DeleteTodoCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id });

        await handler.Handle(new DeleteTodoCommand(todo.Id), CancellationToken.None);

        using var read = db.NewContext();
        (await read.TodoItems.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Delete_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new DeleteTodoCommandHandler(db.NewContext(), new FakeCurrentUserService());

        var act = () => handler.Handle(new DeleteTodoCommand(1), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Delete_OfAnotherUsersTask_ThrowsNotFound()
    {
        using var db = new TestDatabase();
        var me = SeedUser(db);
        var other = SeedUser(db, "other@example.com");
        var theirs = SeedTodo(db, other.Id);

        var handler = new DeleteTodoCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = me.Id });

        var act = () => handler.Handle(new DeleteTodoCommand(theirs.Id), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();

        using var read = db.NewContext();
        (await read.TodoItems.CountAsync()).Should().Be(1);
    }

    // ---- Change status -------------------------------------------------------------

    [Fact]
    public async Task ChangeStatus_MovesTheTaskToTheNewLane()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var todo = SeedTodo(db, user.Id);

        var handler = new ChangeTodoStatusCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id }, _clock);

        var dto = await handler.Handle(
            new ChangeTodoStatusCommand { Id = todo.Id, Status = TodoStatus.Done },
            CancellationToken.None);

        dto.Status.Should().Be(TodoStatus.Done);
    }

    [Fact]
    public async Task ChangeStatus_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new ChangeTodoStatusCommandHandler(
            db.NewContext(), new FakeCurrentUserService(), _clock);

        var act = () => handler.Handle(
            new ChangeTodoStatusCommand { Id = 1, Status = TodoStatus.Done }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task ChangeStatus_ForAMissingTask_ThrowsNotFound()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);

        var handler = new ChangeTodoStatusCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id }, _clock);

        var act = () => handler.Handle(
            new ChangeTodoStatusCommand { Id = 404, Status = TodoStatus.Done }, CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task ChangeStatus_WithTheCurrentToken_Succeeds()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var todo = SeedTodo(db, user.Id);

        var handler = new ChangeTodoStatusCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id }, _clock);

        var dto = await handler.Handle(
            new ChangeTodoStatusCommand
            {
                Id = todo.Id,
                Status = TodoStatus.InProgress,
                ConcurrencyToken = todo.ConcurrencyToken
            },
            CancellationToken.None);

        dto.Status.Should().Be(TodoStatus.InProgress);
    }

    [Fact]
    public async Task ChangeStatus_WithAnEmptyToken_SkipsTheConcurrencyCheck()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var todo = SeedTodo(db, user.Id);

        var handler = new ChangeTodoStatusCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id }, _clock);

        // Guid.Empty means "the client has no token", not "the client saw an empty token".
        var dto = await handler.Handle(
            new ChangeTodoStatusCommand
            {
                Id = todo.Id,
                Status = TodoStatus.InProgress,
                ConcurrencyToken = Guid.Empty
            },
            CancellationToken.None);

        dto.Status.Should().Be(TodoStatus.InProgress);
    }

    [Fact]
    public async Task ChangeStatus_WithAStaleToken_ThrowsConflictCarryingTheCurrentValue()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var todo = SeedTodo(db, user.Id);

        var handler = new ChangeTodoStatusCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id }, _clock);

        var act = () => handler.Handle(
            new ChangeTodoStatusCommand
            {
                Id = todo.Id,
                Status = TodoStatus.Done,
                ConcurrencyToken = Guid.NewGuid()
            },
            CancellationToken.None);

        var conflict = (await act.Should().ThrowAsync<ConcurrencyConflictException>()).Which;
        conflict.CurrentValue.Should().BeOfType<TodoItemDto>();
    }

    [Fact]
    public async Task ChangeStatus_WhenTheRowWasDeletedMeanwhile_ConflictCarriesNoCurrentValue()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var todo = SeedTodo(db, user.Id);

        // The competing delete lands after this handler has read the row but before it writes,
        // so the UPDATE matches nothing and there is no server state left to hand back.
        var context = new RacingDbContext(db.NewContext(), () =>
        {
            using var other = db.NewContext();
            other.TodoItems.Remove(other.TodoItems.Single(t => t.Id == todo.Id));
            other.SaveChanges();
        });

        var handler = new ChangeTodoStatusCommandHandler(
            context, new FakeCurrentUserService { UserId = user.Id }, _clock);

        var act = () => handler.Handle(
            new ChangeTodoStatusCommand
            {
                Id = todo.Id,
                Status = TodoStatus.Done,
                ConcurrencyToken = todo.ConcurrencyToken
            },
            CancellationToken.None);

        var conflict = (await act.Should().ThrowAsync<ConcurrencyConflictException>()).Which;
        conflict.CurrentValue.Should().BeNull();
    }

    // ---- Update --------------------------------------------------------------------

    [Fact]
    public async Task Update_WithAnUnknownCategory_ThrowsNotFound()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var todo = SeedTodo(db, user.Id);

        var handler = new UpdateTodoCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id }, _clock);

        var act = () => handler.Handle(
            new UpdateTodoCommand { Id = todo.Id, Title = "Changed", CategoryId = 404 },
            CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Update_WithAnotherUsersCategory_ThrowsNotFound()
    {
        using var db = new TestDatabase();
        var me = SeedUser(db);
        var other = SeedUser(db, "other@example.com");
        var theirCategory = new Category(other.Id, "Theirs", "#fff", _clock.UtcNow);
        db.Context.Categories.Add(theirCategory);
        db.Context.SaveChanges();
        var todo = SeedTodo(db, me.Id);

        var handler = new UpdateTodoCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = me.Id }, _clock);

        var act = () => handler.Handle(
            new UpdateTodoCommand { Id = todo.Id, Title = "Changed", CategoryId = theirCategory.Id },
            CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Update_WithTheCallersOwnCategory_Succeeds()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var category = new Category(user.Id, "Work", "#fff", _clock.UtcNow);
        db.Context.Categories.Add(category);
        db.Context.SaveChanges();
        var todo = SeedTodo(db, user.Id);

        var handler = new UpdateTodoCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id }, _clock);

        var dto = await handler.Handle(
            new UpdateTodoCommand { Id = todo.Id, Title = "Changed", CategoryId = category.Id },
            CancellationToken.None);

        dto.CategoryId.Should().Be(category.Id);
    }

    [Fact]
    public async Task Update_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new UpdateTodoCommandHandler(
            db.NewContext(), new FakeCurrentUserService(), _clock);

        var act = () => handler.Handle(
            new UpdateTodoCommand { Id = 1, Title = "X" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Update_WhenTheRowWasDeletedMeanwhile_ConflictCarriesNoCurrentValue()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var todo = SeedTodo(db, user.Id);

        var context = new RacingDbContext(db.NewContext(), () =>
        {
            using var other = db.NewContext();
            other.TodoItems.Remove(other.TodoItems.Single(t => t.Id == todo.Id));
            other.SaveChanges();
        });

        var handler = new UpdateTodoCommandHandler(
            context, new FakeCurrentUserService { UserId = user.Id }, _clock);

        var act = () => handler.Handle(
            new UpdateTodoCommand
            {
                Id = todo.Id,
                Title = "Changed",
                ConcurrencyToken = todo.ConcurrencyToken
            },
            CancellationToken.None);

        var conflict = (await act.Should().ThrowAsync<ConcurrencyConflictException>()).Which;
        conflict.CurrentValue.Should().BeNull();
    }

    // ---- Queries -------------------------------------------------------------------

    [Fact]
    public async Task GetTodos_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new GetTodosQueryHandler(db.NewContext(), new FakeCurrentUserService());

        var act = () => handler.Handle(new GetTodosQuery(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task GetTodos_ActiveFilter_ExcludesDone()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        SeedTodo(db, user.Id, "Open");
        SeedTodo(db, user.Id, "Finished", status: TodoStatus.Done);

        var handler = new GetTodosQueryHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id });

        var results = await handler.Handle(
            new GetTodosQuery { Filter = TodoFilter.Active }, CancellationToken.None);

        results.Select(t => t.Title).Should().Equal("Open");
    }

    [Fact]
    public async Task GetTodos_CompletedFilter_KeepsOnlyDone()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        SeedTodo(db, user.Id, "Open");
        SeedTodo(db, user.Id, "Finished", status: TodoStatus.Done);

        var handler = new GetTodosQueryHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id });

        var results = await handler.Handle(
            new GetTodosQuery { Filter = TodoFilter.Completed }, CancellationToken.None);

        results.Select(t => t.Title).Should().Equal("Finished");
    }

    [Fact]
    public async Task GetTodos_SearchMatchesTitleOrDescription()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        SeedTodo(db, user.Id, "Buy milk");
        SeedTodo(db, user.Id, "Errand", description: "pick up milk on the way");
        SeedTodo(db, user.Id, "Unrelated", description: "nothing to see");

        var handler = new GetTodosQueryHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id });

        var results = await handler.Handle(
            new GetTodosQuery { Search = "  milk  " }, CancellationToken.None);

        results.Select(t => t.Title).Should().BeEquivalentTo(["Buy milk", "Errand"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetTodos_BlankSearch_IsIgnored(string search)
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        SeedTodo(db, user.Id, "One");
        SeedTodo(db, user.Id, "Two");

        var handler = new GetTodosQueryHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id });

        var results = await handler.Handle(
            new GetTodosQuery { Search = search }, CancellationToken.None);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetTodos_OrdersByPriorityThenDueDateWithUndatedLast()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        SeedTodo(db, user.Id, "Low", priority: Priority.Low);
        SeedTodo(db, user.Id, "High undated", priority: Priority.High);
        SeedTodo(db, user.Id, "High soon", priority: Priority.High, dueDate: _clock.UtcNow.AddDays(1));

        var handler = new GetTodosQueryHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id });

        var results = await handler.Handle(new GetTodosQuery(), CancellationToken.None);

        results.Select(t => t.Title).Should().Equal("High soon", "High undated", "Low");
    }

    [Fact]
    public async Task GetTodos_ExcludesOtherUsersTasks()
    {
        using var db = new TestDatabase();
        var me = SeedUser(db);
        var other = SeedUser(db, "other@example.com");
        SeedTodo(db, me.Id, "Mine");
        SeedTodo(db, other.Id, "Theirs");

        var handler = new GetTodosQueryHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = me.Id });

        var results = await handler.Handle(new GetTodosQuery(), CancellationToken.None);

        results.Select(t => t.Title).Should().Equal("Mine");
    }

    [Fact]
    public async Task GetTodoById_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new GetTodoByIdQueryHandler(db.NewContext(), new FakeCurrentUserService());

        var act = () => handler.Handle(new GetTodoByIdQuery(1), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Create_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new CreateTodoCommandHandler(
            db.NewContext(), new FakeCurrentUserService(), _clock);

        var act = () => handler.Handle(
            new CreateTodoCommand { Title = "X" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }
}
