using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TodoApp.Domain.Entities;

namespace TodoApp.Infrastructure.Persistence.Configurations;

public class ExternalLoginConfiguration : IEntityTypeConfiguration<ExternalLogin>
{
    public void Configure(EntityTypeBuilder<ExternalLogin> builder)
    {
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Provider)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(l => l.ProviderKey)
            .IsRequired()
            .HasMaxLength(256);

        // One external identity maps to exactly one user.
        builder.HasIndex(l => new { l.Provider, l.ProviderKey }).IsUnique();
        builder.HasIndex(l => l.UserId);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(l => l.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
