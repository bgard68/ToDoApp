using MediatR;
using TodoApp.Application.Categories.Dtos;

namespace TodoApp.Application.Categories.Commands.UpdateCategory;

public record UpdateCategoryCommand : IRequest<CategoryDto>
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Color { get; init; } = "#64748b";
}
