using FluentValidation;

namespace TodoApp.Application.Todos.Commands.ChangeStatus;

public class ChangeTodoStatusCommandValidator : AbstractValidator<ChangeTodoStatusCommand>
{
    public ChangeTodoStatusCommandValidator()
    {
        RuleFor(x => x.Status).IsInEnum().WithMessage("Status is invalid.");
    }
}
