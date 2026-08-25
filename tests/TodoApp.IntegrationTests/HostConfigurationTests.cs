using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TodoApp.Infrastructure.Authentication;
using Xunit;

namespace TodoApp.IntegrationTests;

/// <summary>
/// Hosts the API as a deployed instance would see it: the Production environment (no Swagger,
/// HSTS and HTTPS redirection on, a health-style root) rather than the Development defaults the
/// rest of the suite runs under.
/// </summary>
public sealed class ProductionWebApplicationFactory : CustomWebApplicationFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        base.ConfigureWebHost(builder);
    }
}

public class ProductionHostTests : IClassFixture<ProductionWebApplicationFactory>
{
    private readonly ProductionWebApplicationFactory _factory;

    public ProductionHostTests(ProductionWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task TheRootReportsHealthInsteadOfRedirectingToSwagger()
    {
        var response = await _factory.CreateClient().GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<StatusResponse>();
        body!.Status.Should().Be("ok");
    }

    [Fact]
    public async Task SwaggerIsNotServedOutsideDevelopment()
    {
        var response = await _factory.CreateClient().GetAsync("/swagger/index.html");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheApiStillWorks()
    {
        var client = _factory.CreateClient();

        var registered = await client.RegisterAsync();

        registered.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TheTestHostNeverReachesForThePwnedPasswordsService()
    {
        var options = _factory.Services.GetRequiredService<IOptions<PasswordBreachCheckOptions>>();

        // appsettings.json turns the check on for deployed environments. A test host that inherits
        // that starts calling a third party, and the suite's outcome then depends on connectivity
        // and on whether its passwords are in the corpus. This is the guard on that.
        options.Value.Enabled.Should().BeFalse();
    }

    private sealed record StatusResponse(string Status);
}

/// <summary>
/// Hosts the API with no appsettings file at all, so every configuration section falls back to
/// the defaults compiled into Program.cs. That fallback path is what a deployment gets when a
/// config file fails to ship, and it should still produce a working app.
/// </summary>
public sealed class BareConfigurationWebApplicationFactory : CustomWebApplicationFactory
{
    private readonly string _emptyContentRoot =
        Directory.CreateTempSubdirectory("todoapp-bare-config-").FullName;

    // Nothing: the point of this host is that it reads no configuration of its own.
    protected override IEnumerable<KeyValuePair<string, string?>> TestConfiguration => [];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.UseContentRoot(_emptyContentRoot);
        base.ConfigureWebHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        // Dispose runs on both the sync and async teardown paths, so this is reached twice and a
        // second delete would throw out of test-class cleanup.
        if (disposing && Directory.Exists(_emptyContentRoot))
        {
            Directory.Delete(_emptyContentRoot, recursive: true);
        }
    }
}

public class BareConfigurationHostTests : IClassFixture<BareConfigurationWebApplicationFactory>
{
    private readonly BareConfigurationWebApplicationFactory _factory;

    public BareConfigurationHostTests(BareConfigurationWebApplicationFactory factory)
        => _factory = factory;

    [Fact]
    public async Task TheAppStartsAndServesOnCompiledInDefaults()
    {
        var response = await _factory.CreateClient().GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RegistrationWorksWithoutAnyConfiguredSections()
    {
        var client = _factory.CreateClient();

        var registered = await client.RegisterAsync();

        registered.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task NoDemoUserIsSeededWhenNothingAsksForOne()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "demo@todoapp.local", password = "Password123!" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}

/// <summary>
/// The optional Azure Key Vault configuration source. It is opt-in on <c>KeyVault:Uri</c>, and a
/// URI that is set but unusable has to stop startup — an app that quietly ignores a typo'd vault
/// would come up without the secrets it is supposed to be reading from it.
/// </summary>
public class KeyVaultConfigurationTests
{
    private sealed class KeyVaultFactory : CustomWebApplicationFactory
    {
        private readonly string _uri;

        public KeyVaultFactory(string uri) => _uri = uri;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("KeyVault:Uri", _uri);
            base.ConfigureWebHost(builder);
        }
    }

    [Fact]
    public void AMalformedVaultUriStopsStartup()
    {
        using var factory = new KeyVaultFactory("not a vault uri");

        var act = () => factory.CreateClient();

        act.Should().Throw<UriFormatException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoVaultUriMeansNoAzureCallAtAll(string uri)
    {
        using var factory = new KeyVaultFactory(uri);

        var response = await factory.CreateClient().GetAsync("/api/todos");

        // The app started and served a request without ever reaching for a vault.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
