using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using TodoApp.Infrastructure.Authentication;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// Reading the caller's identity off the request. Inbound claim mapping is off, so the raw JWT
/// claim names ("sub", "email", "role") are what the service must look for.
/// </summary>
public class CurrentUserServiceTests
{
    private static CurrentUserService For(ClaimsPrincipal? principal)
    {
        var accessor = new HttpContextAccessor();

        if (principal is not null)
        {
            accessor.HttpContext = new DefaultHttpContext { User = principal };
        }

        return new CurrentUserService(accessor);
    }

    private static ClaimsPrincipal Authenticated(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType: "Test", nameType: "sub", roleType: "role"));

    [Fact]
    public void WithNoHttpContext_NobodyIsSignedIn()
    {
        var service = For(null);

        service.UserId.Should().BeNull();
        service.Email.Should().BeNull();
        service.IsAuthenticated.Should().BeFalse();
        service.IsInRole("Admin").Should().BeFalse();
    }

    [Fact]
    public void WithAnAnonymousPrincipal_NobodyIsSignedIn()
    {
        var service = For(new ClaimsPrincipal(new ClaimsIdentity()));

        service.UserId.Should().BeNull();
        service.Email.Should().BeNull();
        service.IsAuthenticated.Should().BeFalse();
        service.IsInRole("Admin").Should().BeFalse();
    }

    [Fact]
    public void ReadsTheIdentityFromTheRawJwtClaims()
    {
        var service = For(Authenticated(
            new Claim("sub", "42"),
            new Claim("email", "me@example.com"),
            new Claim("role", "Admin")));

        service.UserId.Should().Be(42);
        service.Email.Should().Be("me@example.com");
        service.IsAuthenticated.Should().BeTrue();
        service.IsInRole("Admin").Should().BeTrue();
        service.IsInRole("User").Should().BeFalse();
    }

    [Fact]
    public void WithAPrincipalThatCarriesNoIdentity_NobodyIsSignedIn()
    {
        // ClaimsPrincipal.Identity is null when no identity has been added at all — a shape the
        // null-conditional chain in IsAuthenticated has to survive.
        var service = For(new ClaimsPrincipal());

        service.IsAuthenticated.Should().BeFalse();
        service.UserId.Should().BeNull();
    }

    [Fact]
    public void ANonNumericSubjectIsNotAUserId()
    {
        var service = For(Authenticated(new Claim("sub", "not-a-number")));

        service.UserId.Should().BeNull();
        service.IsAuthenticated.Should().BeTrue(); // still signed in, just not identifiable
    }
}
