using FluentAssertions;
using TodoApp.Infrastructure.Authentication;
using Xunit;

namespace TodoApp.UnitTests.Security;

/// <summary>
/// Review finding L8 — PBKDF2 work factor raised to OWASP's 600,000, with in-place upgrade of
/// hashes created under the old 100,000.
/// </summary>
public class PasswordHashingTests
{
    private static readonly PasswordHasher Hasher = new();

    [Fact]
    public void New_hashes_use_the_current_iteration_count()
    {
        var hash = Hasher.Hash("correct horse battery staple");

        var iterations = int.Parse(hash.Split('.')[0]);
        iterations.Should().Be(600_000, "OWASP's guidance for PBKDF2-HMAC-SHA256");
    }

    [Fact]
    public void A_freshly_created_hash_does_not_need_rehashing()
    {
        Hasher.NeedsRehash(Hasher.Hash("correct horse battery staple")).Should().BeFalse();
    }

    [Fact]
    public void A_hash_created_at_the_old_work_factor_still_verifies()
    {
        // Exactly what a pre-existing row in the production database looks like: 100k iterations.
        var legacy = LegacyHash("Password123!", 100_000);

        Hasher.Verify(legacy, "Password123!").Should().BeTrue(
            "raising the constant must not lock existing users out");
        Hasher.Verify(legacy, "wrong").Should().BeFalse();
    }

    [Fact]
    public void A_hash_created_at_the_old_work_factor_is_flagged_for_rehash()
    {
        Hasher.NeedsRehash(LegacyHash("Password123!", 100_000)).Should().BeTrue();
    }

    [Fact]
    public void An_unparseable_hash_is_flagged_for_rehash()
    {
        Hasher.NeedsRehash("not-a-hash").Should().BeTrue();
        Hasher.NeedsRehash("").Should().BeTrue();
    }

    [Fact]
    public void Rehashing_produces_a_different_hash_that_verifies_the_same_password()
    {
        const string password = "Password123!";
        var legacy = LegacyHash(password, 100_000);

        var upgraded = Hasher.Hash(password);

        upgraded.Should().NotBe(legacy, "a new random salt is used");
        Hasher.Verify(upgraded, password).Should().BeTrue();
        Hasher.NeedsRehash(upgraded).Should().BeFalse();
    }

    /// <summary>Builds a hash in the stored format at an arbitrary iteration count.</summary>
    private static string LegacyHash(string password, int iterations)
    {
        var salt = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(salt);
        var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        return string.Join('.', iterations, Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }
}
