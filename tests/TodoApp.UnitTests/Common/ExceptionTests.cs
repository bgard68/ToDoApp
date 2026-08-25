using FluentAssertions;
using FluentValidation.Results;
using TodoApp.Application.Common.Exceptions;
using Xunit;

namespace TodoApp.UnitTests.Common;

/// <summary>
/// The exception types are the API's error contract — each one maps to a status code in
/// GlobalExceptionHandler, so their messages and payloads are worth pinning down.
/// </summary>
public class ExceptionTests
{
    [Fact]
    public void NotFound_FromANameAndKey_ReadsAsASentence()
    {
        new NotFoundException("TodoItem", 7).Message
            .Should().Be("Entity \"TodoItem\" (7) was not found.");
    }

    [Fact]
    public void NotFound_FromAMessage_UsesItVerbatim()
    {
        new NotFoundException("That board is gone.").Message.Should().Be("That board is gone.");
    }

    [Fact]
    public void Unauthorized_HasADefaultMessage()
    {
        new UnauthorizedException().Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Unauthorized_KeepsAnExplicitMessage()
    {
        new UnauthorizedException("Invalid refresh token.").Message
            .Should().Be("Invalid refresh token.");
    }

    [Fact]
    public void Forbidden_HasADefaultMessage()
    {
        new ForbiddenAccessException().Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Conflict_KeepsItsMessage()
    {
        new ConflictException("Already exists.").Message.Should().Be("Already exists.");
    }

    [Fact]
    public void ConcurrencyConflict_CarriesTheCurrentServerState()
    {
        var current = new { Id = 1 };

        var exception = new ConcurrencyConflictException("Stale.", current);

        exception.Message.Should().Be("Stale.");
        exception.CurrentValue.Should().BeSameAs(current);
    }

    [Fact]
    public void ConcurrencyConflict_MayCarryNothing()
    {
        new ConcurrencyConflictException("Stale.", null).CurrentValue.Should().BeNull();
    }

    [Fact]
    public void Validation_WithNoFailures_HasNoErrors()
    {
        var exception = new ValidationException();

        exception.Errors.Should().BeEmpty();
        exception.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Validation_GroupsFailuresByPropertyAndDropsDuplicates()
    {
        var exception = new ValidationException(
        [
            new ValidationFailure("Email", "Email is required."),
            new ValidationFailure("Password", "Too short."),
            new ValidationFailure("Password", "Too common."),
            new ValidationFailure("Password", "Too short.")
        ]);

        exception.Errors.Should().HaveCount(2);
        exception.Errors["Email"].Should().Equal("Email is required.");
        exception.Errors["Password"].Should().Equal("Too short.", "Too common.");
    }
}
