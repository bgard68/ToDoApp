using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Ensures the database exists and — when demo seeding is explicitly enabled — creates a demo
/// user with a few sample items on first run. Uses EnsureCreated so the app runs without EF
/// migrations; see the README for switching to migrations in production.
/// </summary>
public static class DbInitializer
{
    public static async Task InitializeAsync(
        ApplicationDbContext context,
        IPasswordHasher passwordHasher,
        IDateTimeProvider dateTime,
        DemoSeedOptions? seed = null,
        bool initializeOnStartup = true)
    {
        // One gate over EVERY database call this method makes, not just the schema check.
        // On a serverless database, opening a connection IS the wake-up, billed as a full
        // minimum interval whether or not anybody visits — so a redeploy that merely asks
        // "does the schema exist?" or "has anyone been seeded?" costs the same as real traffic.
        // Both questions are first-run questions, so both belong behind the same flag: turn it
        // on for the deployment that creates or changes the schema, and leave it off so routine
        // restarts make no database contact at all.
        if (!initializeOnStartup)
        {
            return;
        }

        await context.Database.EnsureCreatedAsync();

        // Seeding is opt-in. Without this guard a fresh production database would come up with a
        // known-credential account (review finding H1).
        if (seed is not { DemoUser: true })
        {
            return;
        }

        if (await context.Users.AnyAsync())
        {
            return;
        }

        var now = dateTime.UtcNow;

        // Fail closed: an enabled seed with no configured password gets an unguessable one rather
        // than a constant baked into the assembly. The account exists (so the sample board renders)
        // but nobody can sign in to it until a real password is configured.
        var password = string.IsNullOrWhiteSpace(seed.Password)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : seed.Password;

        var demo = new User(seed.Email, passwordHasher.Hash(password), now, UserRole.User);
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

        var items = new[]
        {
            new TodoItem(demo.Id, "Welcome to your board", "Drag cards between the To Do, In Progress, and Done lanes.", Priority.Medium, CatId("Other"), null, now),
            new TodoItem(demo.Id, "Buy groceries", "Milk, eggs, coffee.", Priority.Medium, CatId("Errands"), now.AddDays(1), now),
            new TodoItem(demo.Id, "Call the dentist", null, Priority.Low, CatId("Personal"), null, now),
            inProgress,
            done
        };

        context.TodoItems.AddRange(items);
        await context.SaveChangesAsync();
    }
}
