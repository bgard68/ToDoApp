using System.Security.Cryptography;
using TodoApp.Application.Common.Interfaces;

namespace TodoApp.Infrastructure.Authentication;

/// <summary>
/// PBKDF2 (SHA-256) password hasher. Output format: "{iterations}.{saltBase64}.{hashBase64}".
/// Uses a per-password random salt and a constant-time comparison on verify.
/// </summary>
/// <remarks>
/// <para>
/// Iteration count is 600,000 — OWASP's current guidance for PBKDF2-HMAC-SHA256 (review finding
/// L8). It was 100,000.
/// </para>
/// <para>
/// The count is stored <em>inside</em> the hash, so old hashes keep verifying at whatever count
/// they were created with — raising the constant locks nobody out. <see cref="NeedsRehash"/> lets
/// the login path detect a stale hash and upgrade it in place, so accounts migrate as people sign
/// in rather than needing a bulk migration or a forced reset.
/// </para>
/// <para>
/// Argon2id is the stronger primitive but needs a third-party package. PBKDF2 at the recommended
/// count is in the BCL, adds no dependency, and the CPU cost on the Free tier is already bounded
/// by the H3 rate limiter.
/// </para>
/// </remarks>
public class PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;      // 128-bit salt
    private const int KeySize = 32;       // 256-bit derived key
    private const int Iterations = 600_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, Algorithm, KeySize);

        return string.Join('.',
            Iterations,
            Convert.ToBase64String(salt),
            Convert.ToBase64String(key));
    }

    public bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (!TryParse(hash, out var iterations, out var salt, out var key))
        {
            return false;
        }

        var attempt = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, key.Length);
        return CryptographicOperations.FixedTimeEquals(attempt, key);
    }

    /// <summary>
    /// True when <paramref name="hash"/> was produced with weaker parameters than current policy,
    /// so the caller should re-hash the (already verified) password and persist it.
    /// </summary>
    public bool NeedsRehash(string hash)
    {
        if (!TryParse(hash, out var iterations, out var salt, out var key))
        {
            // Unparseable: treat as stale so the next successful sign-in replaces it.
            return true;
        }

        return iterations < Iterations || salt.Length < SaltSize || key.Length < KeySize;
    }

    private static bool TryParse(string hash, out int iterations, out byte[] salt, out byte[] key)
    {
        iterations = 0;
        salt = [];
        key = [];

        var parts = hash.Split('.', 3);
        if (parts.Length != 3 || !int.TryParse(parts[0], out iterations) || iterations <= 0)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[1]);
            key = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && key.Length > 0;
    }
}
