using System.Security.Cryptography;
using System.Text;
using Condux.Core.Billing;
using Xunit;

namespace Condux.Core.Tests;

public class StripeSignatureTests
{
    private const string Secret = "whsec_test_secret";
    private const string Payload = """{"id":"evt_1","type":"checkout.session.completed"}""";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void Verify_accepts_a_valid_recent_signature()
    {
        var header = SignedHeader(Now, Secret);
        Assert.True(StripeSignature.Verify(Payload, header, Secret, Now));
    }

    [Fact]
    public void Verify_accepts_when_one_of_several_v1_signatures_matches()
    {
        // Stripe can send multiple v1 signatures during a signing-secret rotation.
        var t = Now.ToUnixTimeSeconds();
        var good = Hmac(Secret, $"{t}.{Payload}");
        var header = $"t={t},v1=deadbeef,v1={good}";
        Assert.True(StripeSignature.Verify(Payload, header, Secret, Now));
    }

    [Fact]
    public void Verify_rejects_a_signature_from_the_wrong_secret()
    {
        var header = SignedHeader(Now, "whsec_the_wrong_secret");
        Assert.False(StripeSignature.Verify(Payload, header, Secret, Now));
    }

    [Fact]
    public void Verify_rejects_a_stale_timestamp_outside_tolerance()
    {
        var old = Now - TimeSpan.FromMinutes(10);
        var header = SignedHeader(old, Secret); // correctly signed, but too old to replay
        Assert.False(StripeSignature.Verify(Payload, header, Secret, Now));
    }

    [Fact]
    public void Verify_rejects_a_tampered_payload()
    {
        var header = SignedHeader(Now, Secret);
        Assert.False(StripeSignature.Verify(Payload + "tampered", header, Secret, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("t=1700000000")]        // no v1
    [InlineData("v1=abc")]              // no timestamp
    public void Verify_rejects_malformed_headers(string? header) =>
        Assert.False(StripeSignature.Verify(Payload, header, Secret, Now));

    private static string SignedHeader(DateTimeOffset at, string secret)
    {
        var t = at.ToUnixTimeSeconds();
        return $"t={t},v1={Hmac(secret, $"{t}.{Payload}")}";
    }

    private static string Hmac(string secret, string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }
}
