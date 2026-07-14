using MediatR;
using TodoApp.Application.Categories.Dtos;

namespace TodoApp.Application.Categories.Commands.CreateCategory;

public record CreateCategoryCommand : IRequest<CategoryDto>
{
    public string Name { get; init; } = string.Empty;
    public string Color { get; init; } = "#64748b";
}
