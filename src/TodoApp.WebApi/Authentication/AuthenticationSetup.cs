using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Infrastructure.Authentication;

namespace TodoApp.WebApi.Authentication;

public static class AuthenticationSetup
{
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var settings = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();

        if (string.IsNullOrWhiteSpace(settings.Key) || Encoding.UTF8.GetByteCount(settings.Key) < 32)
        {
            throw new InvalidOperationException(
                "Jwt:Key must be configured and at least 32 bytes (256 bits) long. " +
                "Set it via environment variable or user-secrets.");
        }

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Keep raw JWT claim names ("sub", "role", "sstamp") instead of remapping them.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = settings.Issuer,
                    ValidateAudience = true,
                    ValidAudience = settings.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "role"
                };

                // Revocation check: the token's security stamp must still match the user's.
                // Rotating the stamp (compromise / sign-out-everywhere) invalidates every
                // outstanding access token immediately, despite JWTs being stateless.
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        var db = context.HttpContext.RequestServices
                            .GetRequiredService<IApplicationDbContext>();

                        var principal = context.Principal;
                        var sub = principal?.FindFirstValue("sub");
                        var stamp = principal?.FindFirstValue("sstamp");

                        if (!int.TryParse(sub, out var userId) || string.IsNullOrEmpty(stamp))
                        {
                            context.Fail("Invalid token.");
                            return;
                        }

                        var user = await db.Users
                            .AsNoTracking()
                            .FirstOrDefaultAsync(u => u.Id == userId);

                        if (user is null || !user.IsActive || user.SecurityStamp != stamp)
                        {
                            context.Fail("This token has been revoked.");
                        }
                    }
                };
            });

        services.AddAuthorization();

        return services;
    }
}
