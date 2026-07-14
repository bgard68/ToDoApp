using MediatR;
using TodoApp.Application.Categories.Dtos;

namespace TodoApp.Application.Categories.Queries.GetCategories;

public record GetCategoriesQuery : IRequest<IReadOnlyList<CategoryDto>>;
