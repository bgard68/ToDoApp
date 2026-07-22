using MediatR;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Categories.Commands.DeleteCategory;

public class DeleteCategoryCommandHandler : IRequestHandler<DeleteCategoryCommand>
{
    private readonly ICategoryRepository _categories;
    private readonly ICurrentUserService _currentUser;

    public DeleteCategoryCommandHandler(ICategoryRepository categories, ICurrentUserService currentUser)
    {
        _categories = categories;
        _currentUser = currentUser;
    }

    public async Task Handle(DeleteCategoryCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var category = await _categories.GetByIdAsync(request.Id, userId, cancellationToken)
            ?? throw new NotFoundException(nameof(Category), request.Id);

        // Tasks that referenced this category are left uncategorized (FK ON DELETE SET NULL).
        await _categories.DeleteAsync(category.Id, userId, cancellationToken);
    }
}
