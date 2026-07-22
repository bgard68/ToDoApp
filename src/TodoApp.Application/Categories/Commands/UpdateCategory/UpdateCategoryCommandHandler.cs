using MediatR;
using TodoApp.Application.Categories.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Categories.Commands.UpdateCategory;

public class UpdateCategoryCommandHandler : IRequestHandler<UpdateCategoryCommand, CategoryDto>
{
    private readonly ICategoryRepository _categories;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public UpdateCategoryCommandHandler(
        ICategoryRepository categories,
        ICurrentUserService currentUser,
        IDateTimeProvider dateTime)
    {
        _categories = categories;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<CategoryDto> Handle(UpdateCategoryCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var category = await _categories.GetByIdAsync(request.Id, userId, cancellationToken)
            ?? throw new NotFoundException(nameof(Category), request.Id);

        category.Update(request.Name, request.Color, _dateTime.UtcNow);

        try
        {
            await _categories.UpdateAsync(category, cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            throw new ConflictException("A category with this name already exists.");
        }

        return CategoryDto.FromEntity(category);
    }
}
