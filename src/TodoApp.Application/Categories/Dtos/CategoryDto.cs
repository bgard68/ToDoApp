using TodoApp.Domain.Entities;

namespace TodoApp.Application.Categories.Dtos;

public record CategoryDto(int Id, string Name, string Color)
{
    public static CategoryDto FromEntity(Category category) =>
        new(category.Id, category.Name, category.Color);
}
