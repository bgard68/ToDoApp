using FluentValidation;

namespace TodoApp.Application.Categories.Commands.UpdateCategory;

public class UpdateCategoryCommandValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryCommandValidator()
    {
        RuleFor(x => x.Id).GreaterThan(0).WithMessage("A valid id is required.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(50).WithMessage("Name must be 50 characters or fewer.");

        RuleFor(x => x.Color)
            .Matches("^#([0-9A-Fa-f]{6})$").WithMessage("Color must be a hex value like #4f46e5.");
    }
}
