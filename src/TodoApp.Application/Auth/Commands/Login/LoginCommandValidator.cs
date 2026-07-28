using FluentValidation;

namespace TodoApp.Application.Auth.Commands.Login;

public class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .MaximumLength(PasswordPolicy.MaxEmailLength);

        // Bound the password before it reaches PBKDF2. Without a cap, a single request can hand
        // the hasher a multi-megabyte string and burn CPU proportional to its length on every one
        // of the 100k iterations — a cheap denial of service (review finding H3).
        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MaximumLength(PasswordPolicy.MaxLength)
                .WithMessage($"Password must be at most {PasswordPolicy.MaxLength} characters.");
    }
}
