using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using TodoApp.Application.Common.Interfaces;
using TodoApp.Application.Common.Models;

namespace TodoApp.Infrastructure.Authentication;

/// <summary>
/// Validates Google ID tokens using Google's published signing keys. Confirms the
/// signature, expiry, issuer, and that the audience matches our configured client ID.
/// </summary>
public class GoogleTokenValidator : IGoogleTokenValidator
{
    private readonly GoogleAuthSettings _settings;

    public GoogleTokenValidator(IOptions<GoogleAuthSettings> options)
    {
        _settings = options.Value;
    }

    public async Task<GoogleUserInfo?> ValidateAsync(string idToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId))
        {
            throw new InvalidOperationException(
                "Google sign-in is not configured. Set Authentication:Google:ClientId.");
        }

        try
        {
            var settings = new GoogleJsonWebSignature.ValidationSettings
            {
                Audience = new[] { _settings.ClientId }
            };

            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);

            return FromPayload(payload);
        }
        catch (InvalidJwtException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps an already-verified Google payload onto our own model. Split out from
    /// <see cref="ValidateAsync"/> so the mapping is testable without a Google-signed token —
    /// validation itself needs Google's live signing keys and cannot run offline.
    /// </summary>
    public static GoogleUserInfo FromPayload(GoogleJsonWebSignature.Payload payload) => new(
        payload.Subject,
        payload.Email,
        payload.EmailVerified,
        payload.Name);
}
