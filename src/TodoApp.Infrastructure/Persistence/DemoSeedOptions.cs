namespace TodoApp.Infrastructure.Persistence;

/// <summary>
/// Controls the first-run demo account and sample data.
/// </summary>
/// <remarks>
/// Seeding is OFF unless <see cref="Enabled"/> is explicitly set. The demo account exists so a
/// reviewer can sign in to a local (or deliberately public) instance without registering; it is
/// not something a production deployment should get by accident. See
/// <c>docs/development/security-remediation.md</c> (H1).
/// </remarks>
public class DemoSeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>Whether to create the demo user and sample todos when the database is empty.</summary>
    public bool DemoUser { get; set; }

    /// <summary>Email for the demo account.</summary>
    public string Email { get; set; } = "demo@todoapp.local";

    /// <summary>
    /// Password for the demo account. Sourced from configuration (env var / Key Vault) so a
    /// deployed instance never uses a value that is committed to the repository. When seeding is
    /// enabled and this is blank, a random password is generated and the account is unusable for
    /// sign-in — deliberately failing closed rather than falling back to a known constant.
    /// </summary>
    public string Password { get; set; } = string.Empty;
}
