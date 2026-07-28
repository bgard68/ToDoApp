#if DEBUG
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Common.Models;

namespace TodoApp.Infrastructure.Authentication;

/// <summary>
/// DEVELOPMENT-ONLY stand-in for <see cref="GoogleTokenValidator"/>. It lets the Google
/// sign-in flow be exercised end to end (including the create-user success path) without a real
/// Google ID token, so smoke tests and local demos don't need a live Google client.
///
/// A "token" of the form <c>fake:{email}</c> (optionally <c>fake:{email}:{name}</c>) is treated as
/// a verified Google identity; anything else is rejected (returns null), mirroring how the real
/// validator rejects an invalid token.
/// </summary>
/// <remarks>
/// Compiled into DEBUG builds only (review finding M7). It was previously guarded at runtime by
/// <c>IsDevelopment()</c> AND <c>Authentication:Google:UseFake</c> — sound reasoning, but both are
/// app settings, and both are exactly the kind of setting a Provision/Export round-trip copies
/// between environments. Since this type mints a <em>verified</em> identity for any address the
/// caller supplies, the blast radius is authentication bypass as any user, so it now gets a
/// compile-time barrier: Release builds — which is what ships — do not contain it at all.
/// Local development runs Debug, so the smoke test and demo flow are unaffected.
/// </remarks>
public class FakeGoogleTokenValidator : IGoogleTokenValidator
{
    private const string Prefix = "fake:";

    public Task<GoogleUserInfo?> ValidateAsync(string idToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idToken) || !idToken.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return Task.FromResult<GoogleUserInfo?>(null);
        }

        var parts = idToken.Substring(Prefix.Length).Split(':');
        var email = parts[0];
        if (string.IsNullOrWhiteSpace(email))
        {
            return Task.FromResult<GoogleUserInfo?>(null);
        }

        var name = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : "Fake Google User";
        var subject = $"fake-google-{email}";

        return Task.FromResult<GoogleUserInfo?>(new GoogleUserInfo(subject, email, true, name));
    }
}
#endif
