using TodoApp.Domain.Common;

namespace TodoApp.Domain.Entities;

/// <summary>
/// A user-defined task category with a display color. Owned by a <see cref="User"/>.
/// Tasks reference a category by id; deleting a category leaves its tasks uncategorized.
/// </summary>
public class Category : BaseEntity
{
    public int UserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Display color as a hex string, e.g. "#4f46e5".</summary>
    public string Color { get; private set; } = "#64748b";

    private Category() { }

    public Category(int userId, string name, string color, DateTimeOffset now)
    {
        if (userId <= 0)
        {
            throw new ArgumentException("A valid owner is required.", nameof(userId));
        }

        UserId = userId;
        SetName(name);
        SetColor(color);
        CreatedAt = now;
    }

    public void Update(string name, string color, DateTimeOffset now)
    {
        SetName(name);
        SetColor(color);
        UpdatedAt = now;
    }

    private void SetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name is required.", nameof(name));
        }

        Name = name.Trim();
    }

    private void SetColor(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            throw new ArgumentException("Color is required.", nameof(color));
        }

        Color = color.Trim();
    }

    /// <summary>A starter set of categories seeded for each new user.</summary>
    public static IEnumerable<Category> DefaultsFor(int userId, DateTimeOffset now) => new[]
    {
        new Category(userId, "Work", "#7fb2e6", now),
        new Category(userId, "Personal", "#ef9db4", now),
        new Category(userId, "Errands", "#86c97b", now),
        new Category(userId, "Study", "#e3c85a", now),
        new Category(userId, "Other", "#b49ce8", now)
    };
}
