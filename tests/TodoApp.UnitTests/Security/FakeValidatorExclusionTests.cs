using FluentAssertions;
using TodoApp.Infrastructure.Authentication;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// Review finding M7 — <c>FakeGoogleTokenValidator</c> turns <c>fake:{email}</c> into a
/// <em>verified</em> Google identity for any address the caller supplies. It was guarded at
/// runtime by <c>IsDevelopment()</c> AND a config flag; both are app settings, and the blast
/// radius is authentication bypass as any user, so it now has a compile-time barrier too.
/// </summary>
public class FakeValidatorExclusionTests
{
    private const string FakeValidatorTypeName =
        "TodoApp.Infrastructure.Authentication.FakeGoogleTokenValidator";

    [Fact]
    public void The_fake_google_validator_is_not_compiled_into_release_builds()
    {
        // Resolve by name against the shipped assembly rather than by typeof(), which would not
        // compile in Release at all — that is precisely the property under test.
        var infrastructure = typeof(GoogleTokenValidator).Assembly;

        var fake = infrastructure.GetType(FakeValidatorTypeName, throwOnError: false);

#if DEBUG
        fake.Should().NotBeNull(
            "the fake validator must remain available in Debug for the smoke test and local demos");
#else
        fake.Should().BeNull(
            "M7 — a type that mints verified identities from arbitrary input must not exist in the "
            + "Release assembly that ships, regardless of how the app is configured");
#endif
    }
}
