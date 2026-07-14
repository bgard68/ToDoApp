using TodoApp.Application.Common.Models;

namespace TodoApp.Application.Common.Interfaces;

public interface IGoogleTokenValidator
{
    /// <summary>
    /// Verifies a Google ID token (signature, issuer, audience, expiry) and returns the
    /// identity it asserts, or null if the token is invalid.
    /// </summary>
    Task<GoogleUserInfo?> ValidateAsync(string idToken, CancellationToken cancellationToken);
}
