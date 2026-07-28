using FluentValidation;

namespace TodoApp.Application.Auth.Commands.Register;

public class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email is required.")
            .MaximumLength(PasswordPolicy.MaxEmailLength);

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(PasswordPolicy.MinLength)
                .WithMessage($"Password must be at least {PasswordPolicy.MinLength} characters.")
            // Upper bound keeps an attacker from driving PBKDF2 cost with input size (H3).
            .MaximumLength(PasswordPolicy.MaxLength)
                .WithMessage($"Password must be at most {PasswordPolicy.MaxLength} characters.")
            .Matches("[A-Za-z]").WithMessage("Password must contain a letter.")
            .Matches("[0-9]").WithMessage("Password must contain a digit.");
    }
}
