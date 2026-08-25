using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TodoApp.Application.Common.Exceptions;
using TodoApp.WebApi;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// Every application exception has to come out as an RFC 7807 problem with the right status.
/// This is the API's error contract, so each arm of the mapping is pinned down directly rather
/// than only through whichever endpoints happen to raise it.
/// </summary>
public class GlobalExceptionHandlerTests
{
    private static async Task<(bool handled, HttpContext context, ProblemDetails problem)> HandleAsync(
        Exception exception)
    {
        var problemDetailsService = new CapturingProblemDetailsService();
        var handler = new GlobalExceptionHandler(
            problemDetailsService, NullLogger<GlobalExceptionHandler>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/todos/1";

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        problemDetailsService.Captured.Should().NotBeNull();
        return (handled, context, problemDetailsService.Captured!);
    }

    [Fact]
    public async Task ValidationFailures_Become400WithTheFieldErrors()
    {
        var errors = new[]
        {
            new FluentValidation.Results.ValidationFailure("Email", "Email is required.")
        };

        var (handled, context, problem) = await HandleAsync(new ValidationException(errors));

        handled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        problem.Should().BeOfType<ValidationProblemDetails>()
            .Which.Errors.Should().ContainKey("Email");
    }

    [Fact]
    public async Task NotFound_Becomes404()
    {
        var (_, context, problem) = await HandleAsync(new NotFoundException("TodoItem", 1));

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        problem.Title.Should().Be("Resource not found.");
        problem.Detail.Should().Contain("TodoItem");
    }

    [Fact]
    public async Task Unauthorized_Becomes401()
    {
        var (_, context, problem) = await HandleAsync(new UnauthorizedException("Invalid token."));

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        problem.Title.Should().Be("Authentication failed.");
        problem.Detail.Should().Be("Invalid token.");
    }

    [Fact]
    public async Task ForbiddenAccess_Becomes403()
    {
        var (_, context, problem) = await HandleAsync(
            new ForbiddenAccessException("Not your session."));

        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        problem.Title.Should().Be("Access denied.");
        problem.Detail.Should().Be("Not your session.");
    }

    [Fact]
    public async Task Conflict_Becomes409()
    {
        var (_, context, problem) = await HandleAsync(new ConflictException("Already exists."));

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Title.Should().Be("Request conflict.");
    }

    [Fact]
    public async Task ConcurrencyConflict_Becomes409CarryingTheCurrentServerState()
    {
        var current = new { Id = 1, Title = "Server wins" };

        var (_, context, problem) = await HandleAsync(
            new ConcurrencyConflictException("Stale.", current));

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.Extensions.Should().ContainKey("current");
        problem.Extensions["current"].Should().BeSameAs(current);
    }

    [Fact]
    public async Task ConcurrencyConflict_WithNoServerState_OmitsTheExtension()
    {
        var (_, _, problem) = await HandleAsync(new ConcurrencyConflictException("Stale.", null));

        problem.Extensions.Should().NotContainKey("current");
    }

    [Fact]
    public async Task AnUnexpectedException_Becomes500WithoutLeakingDetail()
    {
        var (_, context, problem) = await HandleAsync(
            new InvalidOperationException("connection string user=sa;password=hunter2"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        problem.Title.Should().Be("An unexpected error occurred.");
        problem.Detail.Should().BeNull(); // the internal message never reaches the client
    }

    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetails? Captured { get; private set; }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Captured = context.ProblemDetails;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Captured = context.ProblemDetails;
            return ValueTask.CompletedTask;
        }
    }
}
