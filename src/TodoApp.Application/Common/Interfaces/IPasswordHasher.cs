namespace TodoApp.Application.Common.Interfaces;

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);

    /// <summary>
    /// True when the stored hash was produced with weaker parameters than current policy. The
    /// login path uses this to upgrade a verified password in place (review finding L8), so
    /// raising the work factor migrates accounts as people sign in — no bulk migration, no
    /// forced reset, nobody locked out.
    /// </summary>
    bool NeedsRehash(string hash);
}
