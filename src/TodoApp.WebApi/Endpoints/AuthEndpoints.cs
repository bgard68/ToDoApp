using MediatR;
using TodoApp.Application.Auth.Commands.GoogleSignIn;
using TodoApp.Application.Auth.Commands.Login;
using TodoApp.Application.Auth.Commands.RefreshToken;
using TodoApp.Application.Auth.Commands.Register;
using TodoApp.Application.Auth.Commands.RevokeAllTokens;
using TodoApp.Application.Auth.Commands.RevokeToken;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Auth.Queries.GetCurrentUser;

namespace TodoApp.WebApi.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (RegisterCommand command, ISender sender) =>
        {
            var result = await sender.Send(command);
            return Results.Ok(result);
        })
        .WithName("Register")
        .AllowAnonymous()
        .Produces<AuthResponse>()
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/login", async (LoginCommand command, ISender sender) =>
        {
            var result = await sender.Send(command);
            return Results.Ok(result);
        })
        .WithName("Login")
        .AllowAnonymous()
        .Produces<AuthResponse>()
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/refresh", async (RefreshTokenCommand command, ISender sender) =>
        {
            var result = await sender.Send(command);
            return Results.Ok(result);
        })
        .WithName("RefreshToken")
        .AllowAnonymous()
        .Produces<AuthResponse>()
        .Produces(StatusCodes.Status401Unauthorized);

        // Exchange a Google ID token (obtained on the client) for our own tokens.
        group.MapPost("/google", async (GoogleSignInCommand command, ISender sender) =>
        {
            var result = await sender.Send(command);
            return Results.Ok(result);
        })
        .WithName("GoogleSignIn")
        .AllowAnonymous()
        .Produces<AuthResponse>()
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status401Unauthorized);

        // Logout: revoke the presented refresh token (requires a valid access token).
        group.MapPost("/logout", async (RevokeTokenCommand command, ISender sender) =>
        {
            await sender.Send(command);
            return Results.NoContent();
        })
        .WithName("Logout")
        .RequireAuthorization()
        .Produces(StatusCodes.Status204NoContent);

        // Compromise response: revoke ALL sessions for the current user (or, as Admin, a target user).
        group.MapPost("/revoke-all", async (RevokeAllTokensCommand? command, ISender sender) =>
        {
            await sender.Send(command ?? new RevokeAllTokensCommand());
            return Results.NoContent();
        })
        .WithName("RevokeAllTokens")
        .RequireAuthorization()
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/me", async (ISender sender) =>
        {
            var result = await sender.Send(new GetCurrentUserQuery());
            return Results.Ok(result);
        })
        .WithName("GetCurrentUser")
        .RequireAuthorization()
        .Produces<UserDto>()
        .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }
}
