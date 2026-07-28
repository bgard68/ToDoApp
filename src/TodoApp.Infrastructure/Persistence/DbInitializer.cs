using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Ensures the schema exists (via <see cref="ISchemaInitializer"/>) and — when demo seeding is
/// explicitly enabled — seeds a demo user with a few sample items on first run. Replaces EF's
/// EnsureCreated + change-tracking seed; the demo data is inserted through the repositories
/// inside a single transaction.
/// </summary>
public static class DbInitializer
{
    /// <summary>
    /// Resolves the persistence services from the given (scoped) provider, creates the schema,
    /// and seeds demo data when the database is empty AND seeding was asked for.
    /// </summary>
    public static async Task InitializeAsync(
        IServiceProvider services,
        DemoSeedOptions? seed = null,
        CancellationToken cancellationToken = default)
    {
        var schema = services.GetRequiredService<ISchemaInitializer>();

        await schema.EnsureCreatedAsync(cancellationToken);

        // Seeding is opt-in. Without this guard a fresh production database would come up with a
        // known-credential account (review finding H1).
        if (seed is not { DemoUser: true })
        {
            return;
        }

        var users = services.GetRequiredService<IUserRepository>();
        var categories = services.GetRequiredService<ICategoryRepository>();
        var todos = services.GetRequiredService<ITodoRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var passwordHasher = services.GetRequiredService<IPasswordHasher>();
        var dateTime = services.GetRequiredService<IDateTimeProvider>();

        if (await users.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = dateTime.UtcNow;

        // Fail closed: an enabled seed with no configured password gets an unguessable one rather
        // than a constant baked into the assembly.
        var password = string.IsNullOrWhiteSpace(seed.Password)
            ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            : seed.Password;

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var demo = new User(seed.Email, passwordHasher.Hash(password), now, UserRole.User);
            await users.AddAsync(demo, ct);

            // Seed the demo user's categories first so the todos can reference them by id.
            var categoryList = Category.DefaultsFor(demo.Id, now).ToList();
            await categories.AddRangeAsync(categoryList, ct);

            int CatId(string name) => categoryList.First(c => c.Name == name).Id;

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

            foreach (var item in items)
            {
                await todos.AddAsync(item, ct);
            }
        }, cancellationToken);
    }
}
