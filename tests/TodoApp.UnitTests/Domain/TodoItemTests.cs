using FluentAssertions;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;
using Xunit;

namespace TodoApp.UnitTests.Domain;

public class TodoItemTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WithBlankTitle_Throws(string title)
    {
        var act = () => new TodoItem(1, title, null, Priority.Low, null, null, Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_WithInvalidOwner_Throws()
    {
        var act = () => new TodoItem(0, "Task", null, Priority.Low, null, null, Now);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_StartsInToDoLane_AndStampsFields()
    {
        var item = new TodoItem(1, "  Buy milk  ", "  note  ", Priority.High, 7, null, Now);

        item.Title.Should().Be("Buy milk");
        item.Description.Should().Be("note");
        item.UserId.Should().Be(1);
        item.CategoryId.Should().Be(7);
        item.Status.Should().Be(TodoStatus.ToDo);
        item.IsCompleted.Should().BeFalse();
        item.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void MoveTo_Done_MarksCompletedAndStampsUpdatedAt()
    {
        var item = new TodoItem(1, "Task", null, Priority.Low, null, null, Now);
        item.IsCompleted.Should().BeFalse();

        var later = Now.AddMinutes(5);
        item.MoveTo(TodoStatus.Done, later);

        item.Status.Should().Be(TodoStatus.Done);
        item.IsCompleted.Should().BeTrue();
        item.UpdatedAt.Should().Be(later);
    }

    [Fact]
    public void MoveTo_SameStatus_DoesNotStampUpdatedAt()
    {
        var item = new TodoItem(1, "Task", null, Priority.Low, null, null, Now);

        item.MoveTo(TodoStatus.ToDo, Now.AddMinutes(5)); // already in ToDo

        item.UpdatedAt.Should().BeNull();
    }
}
