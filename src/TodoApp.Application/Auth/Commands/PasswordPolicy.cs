namespace TodoApp.Application.Auth.Commands;

/// <summary>
/// Shared password/email bounds, so the register and login validators cannot drift apart.
/// </summary>
/// <remarks>
/// <see cref="MaxLength"/> is a security control, not a UX preference: PBKDF2 hashes the full
/// input on every iteration, so an unbounded password lets one request spend arbitrary CPU
/// (review finding H3). 128 is comfortably above any real passphrase and well below the point
/// where hashing cost becomes attacker-controlled.
/// </remarks>
public static class PasswordPolicy
{
    public const int MinLength = 8;
    public const int MaxLength = 128;
    public const int MaxEmailLength = 256;
}
