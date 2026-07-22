using MediatR;
using TodoApp.Application.Categories.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;

namespace TodoApp.Application.Categories.Queries.GetCategories;

public class GetCategoriesQueryHandler : IRequestHandler<GetCategoriesQuery, IReadOnlyList<CategoryDto>>
{
    private readonly ICategoryRepository _categories;
    private readonly ICurrentUserService _currentUser;

    public GetCategoriesQueryHandler(ICategoryRepository categories, ICurrentUserService currentUser)
    {
        _categories = categories;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<CategoryDto>> Handle(GetCategoriesQuery request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var categories = await _categories.GetForUserAsync(userId, cancellationToken);

        return categories.Select(CategoryDto.FromEntity).ToList();
    }
}
