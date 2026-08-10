using System.Security.Cryptography;
using Condux.Core.Secrets;
using Xunit;

namespace Condux.Core.Tests;

public class SecretBoxTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Seals_and_opens_round_trip()
    {
        var box = new SecretBox(NewKey());
        const string secret = "sk-ant-api03-super-secret-key";
        Assert.Equal(secret, box.Open(box.Seal(secret)));
    }

    [Fact]
    public void Two_seals_of_the_same_value_differ_but_both_open()
    {
        var box = new SecretBox(NewKey());
        var a = box.Seal("same");
        var b = box.Seal("same");
        Assert.NotEqual(Convert.ToBase64String(a), Convert.ToBase64String(b)); // random nonce
        Assert.Equal("same", box.Open(a));
        Assert.Equal("same", box.Open(b));
    }

    [Fact]
    public void A_tampered_blob_fails_to_open()
    {
        var box = new SecretBox(NewKey());
        var blob = box.Seal("secret");
        blob[^1] ^= 0xFF; // flip a ciphertext bit
        Assert.Throws<AuthenticationTagMismatchException>(() => box.Open(blob));
    }

    [Fact]
    public void A_blob_sealed_under_another_key_does_not_open()
    {
        var blob = new SecretBox(NewKey()).Seal("secret");
        Assert.Throws<AuthenticationTagMismatchException>(() => new SecretBox(NewKey()).Open(blob));
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("dG9vLXNob3J0")] // "too-short" — valid base64 but not 32 bytes
    public void Rejects_an_invalid_master_key(string key) =>
        Assert.Throws<ArgumentException>(() => new SecretBox(key));

    [Fact]
    public void A_blob_with_an_unknown_version_is_rejected()
    {
        var box = new SecretBox(NewKey());
        var blob = box.Seal("secret");
        blob[0] = 0xFF; // a future scheme this build doesn't know
        Assert.Throws<CryptographicException>(() => box.Open(blob));
    }
}
