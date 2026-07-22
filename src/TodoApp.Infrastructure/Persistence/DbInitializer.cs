using Microsoft.Extensions.DependencyInjection;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;
using TodoApp.Domain.Enums;

namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Ensures the schema exists (via <see cref="ISchemaInitializer"/>) and seeds a demo user with a
/// few sample items on first run. Replaces EF's EnsureCreated + change-tracking seed; the demo
/// data is inserted through the repositories inside a single transaction.
/// </summary>
public static class DbInitializer
{
    public const string DemoEmail = "demo@todoapp.local";
    public const string DemoPassword = "Password123!";

    /// <summary>
    /// Resolves the persistence services from the given (scoped) provider, creates the schema,
    /// and seeds demo data when the database is empty.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var schema = services.GetRequiredService<ISchemaInitializer>();
        var users = services.GetRequiredService<IUserRepository>();
        var categories = services.GetRequiredService<ICategoryRepository>();
        var todos = services.GetRequiredService<ITodoRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var passwordHasher = services.GetRequiredService<IPasswordHasher>();
        var dateTime = services.GetRequiredService<IDateTimeProvider>();

        await schema.EnsureCreatedAsync(cancellationToken);

        if (await users.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = dateTime.UtcNow;

        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var demo = new User(DemoEmail, passwordHasher.Hash(DemoPassword), now, UserRole.User);
            await users.AddAsync(demo, ct);

            // Seed the demo user's categories first so the todos can reference them by id.
            var categoryList = Category.DefaultsFor(demo.Id, now).ToList();
            await categories.AddRangeAsync(categoryList, ct);

            int CatId(string name) => categoryList.First(c => c.Name == name).Id;

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

            foreach (var item in seed)
            {
                await todos.AddAsync(item, ct);
            }
        }, cancellationToken);
    }
}
