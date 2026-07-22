using MediatR;
using TodoApp.Application.Categories.Dtos;
using TodoApp.Application.Common.Exceptions;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Domain.Entities;

namespace TodoApp.Application.Categories.Commands.CreateCategory;

public class CreateCategoryCommandHandler : IRequestHandler<CreateCategoryCommand, CategoryDto>
{
    private readonly ICategoryRepository _categories;
    private readonly ICurrentUserService _currentUser;
    private readonly IDateTimeProvider _dateTime;

    public CreateCategoryCommandHandler(
        ICategoryRepository categories,
        ICurrentUserService currentUser,
        IDateTimeProvider dateTime)
    {
        _categories = categories;
        _currentUser = currentUser;
        _dateTime = dateTime;
    }

    public async Task<CategoryDto> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        var userId = _currentUser.UserId ?? throw new UnauthorizedException();

        var category = new Category(userId, request.Name, request.Color, _dateTime.UtcNow);

        try
        {
            await _categories.AddAsync(category, cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            throw new ConflictException("A category with this name already exists.");
        }

        return CategoryDto.FromEntity(category);
    }
}
