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
/// validator rejects an invalid token. This type is wired in ONLY when the app is in the
/// Development environment AND <c>Authentication:Google:UseFake=true</c> — never in production.
/// </summary>
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
