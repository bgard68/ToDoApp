using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TodoApp.Domain.Entities;

namespace TodoApp.Infrastructure.Persistence.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(c => c.Color)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(c => c.CreatedAt)
            .IsRequired();

        // A user can't have two categories with the same name.
        builder.HasIndex(c => new { c.UserId, c.Name }).IsUnique();

        // Deleting a user still removes their categories, but the cascade is done client-side
        // (EF) rather than by the database. This avoids SQL Server's "multiple cascade paths"
        // error: TodoItems is already reachable from Users directly, so a second DB-level
        // cascade from Users through Categories to TodoItems isn't allowed. SQLite doesn't
        // enforce this, which is why it only surfaced on Azure SQL.
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.ClientCascade);
    }
}
