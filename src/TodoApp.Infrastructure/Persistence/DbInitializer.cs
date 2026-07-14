using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Ensures the database exists and seeds a demo user with a few sample items on first
/// run. Uses EnsureCreated so the app runs without EF migrations; see the README for
/// switching to migrations in production.
/// </summary>
public static class DbInitializer
{
    public const string DemoEmail = "demo@todoapp.local";
    public const string DemoPassword = "Password123!";

    public static async Task InitializeAsync(
        ApplicationDbContext context,
        IPasswordHasher passwordHasher,
        IDateTimeProvider dateTime)
    {
        await context.Database.EnsureCreatedAsync();

        if (await context.Users.AnyAsync())
        {
            return;
        }

        var now = dateTime.UtcNow;

        var demo = new User(DemoEmail, passwordHasher.Hash(DemoPassword), now, UserRole.User);
        context.Users.Add(demo);
        await context.SaveChangesAsync();

        // Seed the demo user's categories first so the todos can reference them by id.
        var categories = Category.DefaultsFor(demo.Id, now).ToList();
        context.Categories.AddRange(categories);
        await context.SaveChangesAsync();

        int CatId(string name) => categories.First(c => c.Name == name).Id;

        var inProgress = new TodoItem(demo.Id, "Wire up the board", "Dragging a card to another lane saves its status.", Priority.High, CatId("Work"), now.AddDays(2), now);
        inProgress.MoveTo(TodoStatus.InProgress, now);

        var done = new TodoItem(demo.Id, "Set up the project", "Finished tasks land here — note the check mark.", Priority.Low, CatId("Study"), null, now);
        done.MoveTo(TodoStatus.Done, now);

        var seed = new[]
        {
            new TodoItem(demo.Id, "Welcome to your board", "Drag cards between the To Do, In Progress, and Done lanes.", Priority.Medium, CatId("Other"), null, now),
            new TodoItem(demo.Id, "Buy groceries", "Milk, eggs, coffee.", Priority.Medium, CatId("Errands"), now.AddDays(1), now),
            new TodoItem(demo.Id, "Call the dentist", null, Priority.Low, CatId("Personal"), null, now),
            inProgress,
            done
        };

        context.TodoItems.AddRange(seed);
        await context.SaveChangesAsync();
    }
}
