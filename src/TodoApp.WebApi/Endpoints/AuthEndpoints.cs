using System.Security.Cryptography;
using MediatR;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TodoApp.Application.Auth.Commands.GoogleSignIn;
using TodoApp.Application.Auth.Commands.Login;
using TodoApp.Application.Auth.Commands.RefreshToken;
using TodoApp.Application.Auth.Commands.Register;
using TodoApp.Application.Auth.Commands.RevokeAllTokens;
using TodoApp.Application.Auth.Commands.RevokeToken;
using TodoApp.Application.Auth.Dtos;
using TodoApp.Application.Auth.Queries.GetCurrentUser;
using TodoApp.WebApi.Authentication;

namespace TodoApp.WebApi.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous + password-hashing + token-minting = the endpoints worth brute forcing.
        // Throttled per client IP (review finding H3).
        var group = app.MapGroup("/api/auth")
            .WithTags("Auth")
            .RequireRateLimiting(RateLimitPolicies.Auth);

        // The refresh token now leaves as an httpOnly cookie rather than a JSON field the SPA has
        // to store (review finding H2). AuthOptions.RefreshTokenInBody keeps the old behaviour
        // available for the smoke test and any client mid-migration.
        static AuthResponse Deliver(HttpContext http, AuthResponse response, AuthOptions options)
        {
            var csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            RefreshTokenCookie.Write(http.Response, response.RefreshToken, response.RefreshTokenExpiresAt, csrf);

            return options.RefreshTokenInBody
                ? response
                : response with { RefreshToken = string.Empty };
        }

        group.MapPost("/register", async (RegisterCommand command, ISender sender, HttpContext http, IOptions<AuthOptions> options) =>
        {
            var result = await sender.Send(command);
            return Results.Ok(Deliver(http, result, options.Value));
        })
        .WithName("Register")
        .AllowAnonymous()
        .Produces<AuthResponse>()
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/login", async (LoginCommand command, ISender sender, HttpContext http, IOptions<AuthOptions> options) =>
        {
            var result = await sender.Send(command);
            return Results.Ok(Deliver(http, result, options.Value));
        })
        .WithName("Login")
        .AllowAnonymous()
        .Produces<AuthResponse>()
        .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/refresh", async (RefreshTokenCommand? command, ISender sender, HttpContext http, IOptions<AuthOptions> options) =>
        {
            var token = RefreshTokenCookie.Read(http.Request, command?.RefreshToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return Results.Unauthorized();
            }

            // Double-submit CSRF check for the cookie-borne case (see RefreshTokenCookie).
            if (!RefreshTokenCookie.CsrfSatisfied(http.Request, command?.RefreshToken))
            {
                return Results.Unauthorized();
            }

            var result = await sender.Send(new RefreshTokenCommand { RefreshToken = token });
            return Results.Ok(Deliver(http, result, options.Value));
        })
        .WithName("RefreshToken")
        .AllowAnonymous()
        .Produces<AuthResponse>()
        .Produces(StatusCodes.Status401Unauthorized);

        // Exchange a Google ID token (obtained on the client) for our own tokens.
        group.MapPost("/google", async (GoogleSignInCommand command, ISender sender, HttpContext http, IOptions<AuthOptions> options) =>
        {
            var result = await sender.Send(command);
            return Results.Ok(Deliver(http, result, options.Value));
        })
        .WithName("GoogleSignIn")
        .AllowAnonymous()
        .Produces<AuthResponse>()
        .ProducesValidationProblem()
        .Produces(StatusCodes.Status401Unauthorized);

        // Logout: revoke the presented refresh token (requires a valid access token).
        group.MapPost("/logout", async (RevokeTokenCommand? command, ISender sender, HttpContext http) =>
        {
            var token = RefreshTokenCookie.Read(http.Request, command?.RefreshToken);
            if (!string.IsNullOrWhiteSpace(token))
            {
                await sender.Send(new RevokeTokenCommand { RefreshToken = token });
            }

            RefreshTokenCookie.Clear(http.Response);
            return Results.NoContent();
        })
        .WithName("Logout")
        .RequireAuthorization()
        .Produces(StatusCodes.Status204NoContent);

        // Compromise response: revoke ALL sessions for the current user (or, as Admin, a target user).
        group.MapPost("/revoke-all", async (RevokeAllTokensCommand? command, ISender sender, HttpContext http) =>
        {
            await sender.Send(command ?? new RevokeAllTokensCommand());
            RefreshTokenCookie.Clear(http.Response);
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

/// <summary>Auth delivery settings (config section "Auth").</summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Also return the refresh token in the JSON body. Off by default: the token belongs in the
    /// httpOnly cookie (review finding H2). Kept as an escape hatch for the PowerShell smoke test
    /// and any client that has not migrated.
    /// </summary>
    public bool RefreshTokenInBody { get; set; }
}
