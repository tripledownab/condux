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
