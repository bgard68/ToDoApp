using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Categories.Commands.CreateCategory;
using TodoApp.Application.Categories.Commands.DeleteCategory;
using TodoApp.Application.Categories.Commands.UpdateCategory;
using TodoApp.Application.Categories.Queries.GetCategories;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Domain.Entities;
using TodoApp.UnitTests.TestSupport;
using Xunit;

namespace TodoApp.UnitTests.Categories;

public class CategoryHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new();

    private User SeedUser(TestDatabase db, string email = "cat@example.com")
    {
        var user = new User(email, "hash", _clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();
        return user;
    }

    private Category SeedCategory(TestDatabase db, int userId, string name = "Work")
    {
        var category = new Category(userId, name, "#fff", _clock.UtcNow);
        db.Context.Categories.Add(category);
        db.Context.SaveChanges();
        return category;
    }

    // ---- Update --------------------------------------------------------------------

    [Fact]
    public async Task Update_RenamesAndRecolors()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        var category = SeedCategory(db, user.Id);

        var handler = new UpdateCategoryCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id }, _clock);

        var dto = await handler.Handle(
            new UpdateCategoryCommand { Id = category.Id, Name = "Studies", Color = "#123456" },
            CancellationToken.None);

        dto.Name.Should().Be("Studies");
        dto.Color.Should().Be("#123456");

        using var read = db.NewContext();
        (await read.Categories.SingleAsync(c => c.Id == category.Id)).Name.Should().Be("Studies");
    }

    [Fact]
    public async Task Update_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new UpdateCategoryCommandHandler(
            db.NewContext(), new FakeCurrentUserService(), _clock);

        var act = () => handler.Handle(
            new UpdateCategoryCommand { Id = 1, Name = "X", Color = "#fff" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Update_OfAnotherUsersCategory_ThrowsNotFound()
    {
        using var db = new TestDatabase();
        var me = SeedUser(db);
        var other = SeedUser(db, "other@example.com");
        var theirs = SeedCategory(db, other.Id);

        var handler = new UpdateCategoryCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = me.Id }, _clock);

        var act = () => handler.Handle(
            new UpdateCategoryCommand { Id = theirs.Id, Name = "Mine now", Color = "#fff" },
            CancellationToken.None);

        // Not Forbidden: the caller must not learn that someone else's category exists.
        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Update_ToADuplicateName_ThrowsConflict()
    {
        using var db = new TestDatabase();
        var user = SeedUser(db);
        SeedCategory(db, user.Id, "Work");
        var personal = SeedCategory(db, user.Id, "Personal");

        var handler = new UpdateCategoryCommandHandler(
            db.NewContext(), new FakeCurrentUserService { UserId = user.Id }, _clock);

        var act = () => handler.Handle(
            new UpdateCategoryCommand { Id = personal.Id, Name = "Work", Color = "#fff" },
            CancellationToken.None);

        await act.Should().ThrowAsync<ConflictException>();
    }

    // ---- Create / Delete / Query ---------------------------------------------------

    [Fact]
    public async Task Create_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new CreateCategoryCommandHandler(
            db.NewContext(), new FakeCurrentUserService(), _clock);

        var act = () => handler.Handle(
            new CreateCategoryCommand { Name = "X", Color = "#fff" }, CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Delete_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new DeleteCategoryCommandHandler(
            db.NewContext(), new FakeCurrentUserService());

        var act = () => handler.Handle(new DeleteCategoryCommand(1), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task GetCategories_WithoutAuthenticatedUser_Throws()
    {
        using var db = new TestDatabase();
        var handler = new GetCategoriesQueryHandler(
            db.NewContext(), new FakeCurrentUserService());

        var act = () => handler.Handle(new GetCategoriesQuery(), CancellationToken.None);

        await act.Should().ThrowAsync<UnauthorizedException>();
    }
}
