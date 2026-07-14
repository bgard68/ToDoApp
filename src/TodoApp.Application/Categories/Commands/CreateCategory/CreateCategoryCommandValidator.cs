using FluentValidation;

namespace TodoApp.Application.Categories.Commands.CreateCategory;

public class CreateCategoryCommandValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(50).WithMessage("Name must be 50 characters or fewer.");

        RuleFor(x => x.Color)
            .Matches("^#([0-9A-Fa-f]{6})$").WithMessage("Color must be a hex value like #4f46e5.");
    }
}
