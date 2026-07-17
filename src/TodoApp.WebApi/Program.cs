using Azure.Identity;
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
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUri),
        new DefaultAzureCredential());
}

const string CorsPolicy = "FrontendPolicy";

var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? new[] { "http://localhost:5173" };

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicy, policy =>
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod());
});

// Application + Infrastructure layers (Clean Architecture composition root).
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

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

// Create and seed the database on startup — but never let a cold/paused database (e.g. Azure
// SQL serverless waking from auto-pause) block the app from starting. If the DB is unreachable
// here, we log and carry on; the schema/seed is retried in the background until it succeeds, and
// requests ride out the wake-up via EF's EnableRetryOnFailure.
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var startupLogger = services.GetRequiredService<ILogger<Program>>();
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        var passwordHasher = services.GetRequiredService<IPasswordHasher>();
        var dateTime = services.GetRequiredService<IDateTimeProvider>();
        await DbInitializer.InitializeAsync(context, passwordHasher, dateTime);
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex,
            "Database initialization was deferred at startup (database may be resuming). " +
            "It will be retried in the background.");

        // Retry the initialization off the startup path so the app can start serving immediately.
        _ = Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= 10; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(15));
                try
                {
                    using var retryScope = app.Services.CreateScope();
                    var rs = retryScope.ServiceProvider;
                    await DbInitializer.InitializeAsync(
                        rs.GetRequiredService<ApplicationDbContext>(),
                        rs.GetRequiredService<IPasswordHasher>(),
                        rs.GetRequiredService<IDateTimeProvider>());
                    startupLogger.LogInformation("Database initialization completed on background attempt {Attempt}.", attempt);
                    break;
                }
                catch (Exception retryEx)
                {
                    startupLogger.LogWarning(retryEx, "Background database initialization attempt {Attempt} failed.", attempt);
                }
            }
        });
    }
}

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(CorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapCategoryEndpoints();
app.MapTodoEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger"))
   .ExcludeFromDescription();

app.Run();

// Exposed so WebApplicationFactory<Program> can host the app in integration tests.
public partial class Program { }
