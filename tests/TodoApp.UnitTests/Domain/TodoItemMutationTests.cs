using FluentAssertions;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;
using Xunit;

namespace TodoApp.UnitTests.Domain;

/// <summary>
/// Covers TodoItem.Update and the concurrency-token contract. The existing TodoItemTests
/// exercises the constructor and MoveTo; Update, description normalisation and token
/// rotation were untested, so a mutation to any of them survived the suite.
/// </summary>
public class TodoItemMutationTests
{
    private static readonly DateTimeOffset CreatedOn = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedOn = new(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DueOn = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static TodoItem NewItem() =>
        new(userId: 7, title: "Original", description: "Original notes",
            priority: Priority.Medium, categoryId: 3, dueDate: null, now: CreatedOn);

    // ---- Update: happy path --------------------------------------------------------

    [Fact]
    public void Update_WithAllFieldsChanged_AppliesEveryFieldAndStampsUpdatedAt()
    {
        var item = NewItem();

        item.Update("Revised", "Revised notes", Priority.High, categoryId: 9, dueDate: DueOn, now: UpdatedOn);

        item.Title.Should().Be("Revised");
        item.Description.Should().Be("Revised notes");
        item.Priority.Should().Be(Priority.High);
        item.CategoryId.Should().Be(9);
        item.DueDate.Should().Be(DueOn);
        item.UpdatedAt.Should().Be(UpdatedOn);
    }

    // ---- Update: normalisation -----------------------------------------------------

    [Fact]
    public void Update_WithUntrimmedTitle_StoresTheTrimmedValue()
    {
        var item = NewItem();

        item.Update("  Revised  ", null, Priority.Low, null, null, UpdatedOn);

        item.Title.Should().Be("Revised");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Update_WithWhitespaceOnlyDescription_CollapsesItToNull(string description)
    {
        var item = NewItem();

        item.Update("Revised", description, Priority.Low, null, null, UpdatedOn);

        item.Description.Should().BeNull();
    }

    [Fact]
    public void Update_WithUntrimmedDescription_StoresTheTrimmedValue()
    {
        var item = NewItem();

        item.Update("Revised", "  spaced notes  ", Priority.Low, null, null, UpdatedOn);

        item.Description.Should().Be("spaced notes");
    }

    // ---- Update: clearing optional fields ------------------------------------------

    [Fact]
    public void Update_WithNullCategoryAndDueDate_ClearsBothPreviouslySetValues()
    {
        var item = new TodoItem(7, "Original", "notes", Priority.Medium, categoryId: 3, dueDate: DueOn, now: CreatedOn);

        item.Update("Revised", "notes", Priority.Medium, categoryId: null, dueDate: null, now: UpdatedOn);

        item.CategoryId.Should().BeNull();
        item.DueDate.Should().BeNull();
    }

    // ---- Update: concurrency token --------------------------------------------------

    [Fact]
    public void Update_WhenApplied_RotatesTheConcurrencyToken()
    {
        var item = NewItem();
        var before = item.ConcurrencyToken;

        item.Update("Revised", null, Priority.Low, null, null, UpdatedOn);

        item.ConcurrencyToken.Should().NotBe(before);
        item.ConcurrencyToken.Should().NotBe(Guid.Empty);
    }

    // ---- Update: invariants it must NOT touch ---------------------------------------

    [Fact]
    public void Update_WhenApplied_LeavesStatusAndOwnerAndCreatedAtUntouched()
    {
        var item = NewItem();
        item.MoveTo(TodoStatus.InProgress, CreatedOn);

        item.Update("Revised", null, Priority.High, null, null, UpdatedOn);

        item.Status.Should().Be(TodoStatus.InProgress);
        item.IsCompleted.Should().BeFalse();
        item.UserId.Should().Be(7);
        item.CreatedAt.Should().Be(CreatedOn);
    }

    // ---- Update: negative cases -----------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Update_WithBlankTitle_ThrowsArgumentExceptionNamingTitle(string title)
    {
        var item = NewItem();

        var act = () => item.Update(title, "notes", Priority.High, 9, DueOn, UpdatedOn);

        act.Should().Throw<ArgumentException>()
            .WithMessage("Title is required.*")
            .And.ParamName.Should().Be("title");
    }

    [Fact]
    public void Update_WithBlankTitle_LeavesEveryOtherFieldUnmodified()
    {
        var item = NewItem();

        var act = () => item.Update("", "Revised notes", Priority.High, 9, DueOn, UpdatedOn);

        act.Should().Throw<ArgumentException>();
        item.Title.Should().Be("Original");
        item.Description.Should().Be("Original notes");
        item.Priority.Should().Be(Priority.Medium);
        item.CategoryId.Should().Be(3);
        item.DueDate.Should().BeNull();
        item.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void Update_WithBlankTitle_DoesNotRotateTheConcurrencyToken()
    {
        var item = NewItem();
        var before = item.ConcurrencyToken;

        var act = () => item.Update("   ", null, Priority.Low, null, null, UpdatedOn);

        act.Should().Throw<ArgumentException>();
        item.ConcurrencyToken.Should().Be(before);
    }

    [Fact]
    public void Update_WithNullTitle_ThrowsArgumentExceptionNamingTitle()
    {
        var item = NewItem();

        var act = () => item.Update(null!, null, Priority.Low, null, null, UpdatedOn);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("title");
    }

    // ---- MoveTo: token contract (the no-op branch) -----------------------------------

    [Fact]
    public void MoveTo_TheLaneItIsAlreadyIn_DoesNotRotateTheConcurrencyToken()
    {
        var item = NewItem();
        var before = item.ConcurrencyToken;

        item.MoveTo(TodoStatus.ToDo, UpdatedOn);

        item.ConcurrencyToken.Should().Be(before);
        item.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public void MoveTo_ADifferentLane_RotatesTheConcurrencyToken()
    {
        var item = NewItem();
        var before = item.ConcurrencyToken;

        item.MoveTo(TodoStatus.Done, UpdatedOn);

        item.ConcurrencyToken.Should().NotBe(before);
        item.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void MoveTo_BackOutOfDone_ClearsIsCompleted()
    {
        var item = NewItem();
        item.MoveTo(TodoStatus.Done, UpdatedOn);

        item.MoveTo(TodoStatus.InProgress, UpdatedOn);

        item.Status.Should().Be(TodoStatus.InProgress);
        item.IsCompleted.Should().BeFalse();
    }

    // ---- Constructor: owner guard ---------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Constructor_WithNonPositiveOwner_ThrowsArgumentExceptionNamingUserId(int userId)
    {
        var act = () => new TodoItem(userId, "Title", null, Priority.Low, null, null, CreatedOn);

        act.Should().Throw<ArgumentException>()
            .WithMessage("A valid owner is required.*")
            .And.ParamName.Should().Be("userId");
    }
}
