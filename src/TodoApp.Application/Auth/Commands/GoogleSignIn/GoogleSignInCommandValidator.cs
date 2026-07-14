using FluentValidation;

namespace TodoApp.Application.Auth.Commands.GoogleSignIn;

public class GoogleSignInCommandValidator : AbstractValidator<GoogleSignInCommand>
{
    public GoogleSignInCommandValidator()
    {
        RuleFor(x => x.IdToken).NotEmpty().WithMessage("A Google ID token is required.");
    }
}
