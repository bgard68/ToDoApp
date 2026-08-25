using System.Threading.RateLimiting;
using Azure.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using TodoApp.Application;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Infrastructure;
using TodoApp.Infrastructure.Persistence;
using TodoApp.WebApi;
using TodoApp.WebApi.Authentication;
using TodoApp.WebApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Optional Azure Key Vault configuration source.
// Opt in ONLY when a vault URI is configured. When KeyVault:Uri is unset (local dev, CI, tests,
// or any environment without a vault), this block is skipped entirely — no Azure call, no
// credential lookup, no startup delay — and configuration falls back to user-secrets / env vars /
// appsettings exactly as before. When it IS set (an app setting in Azure), the vault is added last
// so its secrets override the earlier providers, and Jwt:Key resolves from the vault automatically.
// No consuming code changes: AuthenticationSetup still just reads Jwt:Key, and its fail-fast guard
// catches the case where neither the vault nor any other source supplies the key.
//
// Authenticate with the App Service system-assigned managed identity directly via
// ManagedIdentityCredential — NOT DefaultAzureCredential. DefaultAzureCredential probes ~8
// credential sources in sequence (environment, workload identity, dev tooling, etc.); on App
// Service those probes can stall, and the stacked timeouts blow the container's ~230s startup
// limit, causing a hang rather than a clear error. ManagedIdentityCredential targets exactly the
// identity that exists in Azure, so it resolves fast and, if the vault is unreachable/denied,
// fails fast with a real exception instead of hanging. This block only runs when KeyVault:Uri is
// set (Azure), so local development — which leaves the URI unset — is unaffected.
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUri),
        new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));
}

const string CorsPolicy = "FrontendPolicy";

// Rate limiting. The auth endpoints are anonymous and each login runs 100k PBKDF2 iterations, so
// without a limiter they are both a credential-stuffing surface and a cheap way to saturate the
// (Free tier, single instance) CPU — review finding H3. Limits are configurable so a deployment
// can tune them and tests can drive them.
var authLimit = builder.Configuration.GetSection("RateLimiting:Auth").Get<RateLimitOptions>() ?? new RateLimitOptions(10, 60);
var globalLimit = builder.Configuration.GetSection("RateLimiting:Global").Get<RateLimitOptions>() ?? new RateLimitOptions(200, 60);

// Behind a reverse proxy every request appears to come from the proxy, which would collapse all
// users into one partition and throttle everyone at once. Azure App Service APPENDS the observed
// client IP to X-Forwarded-For, so the LAST entry is the platform's assertion — but only trust it
// where such a proxy actually exists, otherwise a client can forge the header to dodge the limit.
var trustForwardedFor = builder.Configuration.GetValue<bool>("RateLimiting:TrustForwardedFor");

string ClientKey(HttpContext http)
{
    if (trustForwardedFor &&
        http.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) &&
        forwarded.Count > 0)
    {
        var hops = forwarded.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (hops.Length > 0)
        {
            // Strip the :port Azure includes, and drop any IPv6 brackets.
            var last = hops[^1];
            var colon = last.LastIndexOf(':');
            if (colon > 0 && last.IndexOf(':') == colon)
            {
                last = last[..colon];
            }
            return last.Trim('[', ']');
        }
    }

    return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.Auth, http =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = authLimit.PermitLimit,
            Window = TimeSpan.FromSeconds(authLimit.WindowSeconds),
            QueueLimit = 0
        }));

    // Backstop for everything else, so an authenticated caller can't hammer the API either.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = globalLimit.PermitLimit,
            Window = TimeSpan.FromSeconds(globalLimit.WindowSeconds),
            QueueLimit = 0
        }));

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter =
            ((int)TimeSpan.FromSeconds(authLimit.WindowSeconds).TotalSeconds).ToString();
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsync(
            """{"title":"Too many requests.","status":429,"detail":"Rate limit exceeded. Try again shortly."}""",
            cancellationToken);
    };
});

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              // Required for the httpOnly refresh cookie to be sent on cross-site requests
              // (review finding H2). Safe because the origin list is an explicit allow-list —
              // AllowCredentials cannot be combined with a wildcard origin, and ASP.NET Core
              // throws at startup if anyone tries.
              .AllowCredentials());
});

// Application + Infrastructure layers (Clean Architecture composition root).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Refresh-token delivery (review finding H2): httpOnly cookie by default, with an opt-in to
// also return it in the body for clients that have not migrated.
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

// JWT authentication + authorization (with security-stamp revocation check).
builder.Services.AddJwtAuthentication(builder.Configuration);

// Consistent RFC 7807 error responses.
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

// OpenAPI / Swagger with bearer support.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token (without the 'Bearer ' prefix)."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Demo seeding is opt-in and never implicit (review finding H1). appsettings.json ships
// DemoUser=false for every environment; appsettings.Development.json turns it on for local work.
// A deployed instance that wants the demo account must set Seed__DemoUser and Seed__Password
// explicitly, and the password comes from configuration rather than a constant in the assembly.
var demoSeed = builder.Configuration.GetSection(DemoSeedOptions.SectionName).Get<DemoSeedOptions>()
    ?? new DemoSeedOptions();

// Create and seed the database on startup — but never let a cold/paused database (e.g. Azure
// SQL serverless waking from auto-pause) block the app from starting. If the DB is unreachable
// here, we log and carry on; the schema/seed is retried in the background until it succeeds, and
// requests ride out the wake-up via EF's EnableRetryOnFailure. The returned background task is
// deliberately not awaited: startup must not wait on it.
_ = await DatabaseStartup.InitializeAsync(
    app.Services,
    demoSeed,
    app.Services.GetRequiredService<ILogger<Program>>(),
    retryDelay: TimeSpan.FromSeconds(15),
    maxRetryAttempts: 10);

app.UseExceptionHandler();

// Baseline response headers on everything, including error responses (review finding H2).
app.UseSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    // App Service already sets httpsOnly, but the app should not depend on the host for this.
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(CorsPolicy);

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapCategoryEndpoints();
app.MapTodoEndpoints();

// Swagger is only mapped in development; elsewhere the redirect just produced a 404 (finding L13).
if (app.Environment.IsDevelopment())
{
    app.MapGet("/", () => Results.Redirect("/swagger"))
       .ExcludeFromDescription();
}
else
{
    app.MapGet("/", () => Results.Ok(new { status = "ok" }))
       .ExcludeFromDescription();
}

app.Run();

// Exposed so WebApplicationFactory<Program> can host the app in integration tests.
public partial class Program { }
