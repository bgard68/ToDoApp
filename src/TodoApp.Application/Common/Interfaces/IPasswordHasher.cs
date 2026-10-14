namespace TodoApp.Application.Common.Interfaces;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);

    /// <summary>
    /// Burns the same work a real <see cref="Verify"/> costs, against a throwaway hash, and
    /// discards the result.
    /// </summary>
    /// <remarks>
    /// For the sign-in path to take the same time whether or not the email exists. Returning in
    /// microseconds for an unknown address and ~100ms for a known one is a reliable
    /// account-enumeration oracle (review finding L4). The decoy hash belongs to the hasher, not
    /// to a handler: only the hasher knows the current work factor, and it is the component whose
    /// lifetime DI already manages — so the cost tracks a policy change for free and no caller
    /// has to keep static state to avoid recomputing it.
    /// </remarks>
    void VerifyDecoy(string password);

    /// <summary>
    /// True when the stored hash was produced with weaker parameters than current policy. The
    /// login path uses this to upgrade a verified password in place (review finding L8), so
    /// raising the work factor migrates accounts as people sign in — no bulk migration, no
    /// forced reset, nobody locked out.
    /// </summary>
    bool NeedsRehash(string hash);
}
