using System.Diagnostics;
using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_then_Verify_accepts_correct_password()
    {
        var hash = PasswordHasher.Hash("correct horse battery staple");
        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_rejects_wrong_password()
    {
        var hash = PasswordHasher.Hash("s3cret-password");
        Assert.False(PasswordHasher.Verify("s3cret-passwerd", hash));
    }

    [Fact]
    public void Hash_is_salted_so_same_password_hashes_differ()
    {
        Assert.NotEqual(PasswordHasher.Hash("same-password"), PasswordHasher.Hash("same-password"));
    }

    [Fact]
    public void Encoding_is_argon2id_phc_and_carries_its_parameters()
    {
        // $argon2id$v=19$m=<kib>,t=<iters>,p=<par>$<salt>$<hash>
        var parts = PasswordHasher.Hash("whatever").Split('$');
        Assert.Equal(6, parts.Length);
        Assert.Equal("argon2id", parts[1]);
        Assert.Equal("v=19", parts[2]);
        Assert.Equal("m=19456,t=2,p=1", parts[3]);
    }

    [Fact]
    public void VerifyOrDecoy_rejects_an_account_that_has_no_hash()
    {
        Assert.False(PasswordHasher.VerifyOrDecoy("password", null));
        Assert.True(PasswordHasher.VerifyOrDecoy("password", PasswordHasher.Hash("password")));
    }

    // The point of the decoy is cost, not the answer: an address with no account must not answer faster
    // than one with a password, or the status code says nothing while the clock says "no such account".
    // Asserting the answer alone would pass against a plain `return false`, which is the bug.
    //
    // A FLOOR, never a ceiling. A ceiling on a deliberately slow operation fails on a loaded runner and
    // gets deleted; this can only fail if the derivation genuinely did not happen. The real cost is tens
    // of milliseconds at 19 MiB, and an early return is microseconds, so the bound is far from both.
    [Fact]
    public void VerifyOrDecoy_still_derives_a_key_when_there_is_no_hash()
    {
        PasswordHasher.VerifyOrDecoy("warm-up", null); // the decoy is built once, so do not time that

        var started = Stopwatch.StartNew();
        PasswordHasher.VerifyOrDecoy("password", null);
        Assert.True(started.ElapsedMilliseconds >= 5, $"took {started.ElapsedMilliseconds}ms");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$600000$c2FsdA==$aGFzaA==")]        // a foreign (non-argon2) hash
    [InlineData("$argon2id$v=19$m=19456,t=2,p=1$c2FsdA==")]       // missing the hash segment
    [InlineData("$argon2id$v=19$m=x,t=2,p=1$c2FsdA==$aGFzaA==")]  // non-numeric cost parameter
    public void Verify_returns_false_for_malformed_input(string encoded)
    {
        Assert.False(PasswordHasher.Verify("password", encoded));
    }
}
