using System.Text;
using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// TOTP against RFC 6238's own published vectors, so the library underneath is checked against the
/// specification rather than taken on trust and a version bump that changed behaviour fails here.
/// </summary>
public class TotpTests
{
    // RFC 6238 Appendix B. The SHA-1 vectors use the ASCII seed "12345678901234567890" and 8 digits.
    private const string RfcSeed = "12345678901234567890";

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_the_rfc6238_reference_vectors(long unixSeconds, string expected)
    {
        var secret = Base32OfSeed();
        var step = unixSeconds / Totp.StepSeconds;

        Assert.Equal(expected, Totp.Compute(secret, step, digits: 8));
    }

    [Fact]
    public void Accepts_the_current_code_and_reports_its_step()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.Compute(secret, Totp.StepAt(now));

        Assert.True(Totp.TryValidate(secret, code, now, out var matched));
        Assert.Equal(Totp.StepAt(now), matched);
    }

    [Fact]
    public void Accepts_one_step_of_drift_either_side()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var current = Totp.StepAt(now);

        Assert.True(Totp.TryValidate(secret, Totp.Compute(secret, current - 1), now, out var behind));
        Assert.Equal(current - 1, behind);
        Assert.True(Totp.TryValidate(secret, Totp.Compute(secret, current + 1), now, out var ahead));
        Assert.Equal(current + 1, ahead);
    }

    // Deliberate: this fails if someone widens the drift window, which silently lengthens the period an
    // observed code stays usable in.
    [Fact]
    public void Refuses_drift_beyond_the_window()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var current = Totp.StepAt(now);

        Assert.False(Totp.TryValidate(secret, Totp.Compute(secret, current - 2), now, out _));
        Assert.False(Totp.TryValidate(secret, Totp.Compute(secret, current + 2), now, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]     // too short
    [InlineData("1234567")]   // too long
    [InlineData("12345a")]    // not digits
    public void Refuses_a_malformed_code(string? code)
    {
        var secret = Totp.NewSecret();

        Assert.False(Totp.TryValidate(secret, code, DateTimeOffset.UnixEpoch, out _));
    }

    [Fact]
    public void One_users_secret_never_validates_anothers_code()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var mine = Totp.NewSecret();
        var theirs = Totp.NewSecret();

        Assert.False(Totp.TryValidate(mine, Totp.Compute(theirs, Totp.StepAt(now)), now, out _));
    }

    [Fact]
    public void Provisioning_uri_carries_what_an_authenticator_reads()
    {
        var uri = Totp.ProvisioningUri("JBSWY3DPEHPK3PXP", "someone@example.com", "Condux");

        Assert.StartsWith("otpauth://totp/Condux:someone%40example.com?", uri, StringComparison.Ordinal);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri, StringComparison.Ordinal);
        Assert.Contains("issuer=Condux", uri, StringComparison.Ordinal);
        Assert.Contains("algorithm=SHA1", uri, StringComparison.Ordinal);
        Assert.Contains("digits=6", uri, StringComparison.Ordinal);
        Assert.Contains("period=30", uri, StringComparison.Ordinal);
    }

    /// <summary>
    /// The RFC seed as base32, encoded HERE rather than by calling the library's own encoder.
    /// </summary>
    /// <remarks>
    /// This duplication is load-bearing, so do not tidy it away. Using OtpCore's encoder to build the
    /// input for OtpCore's decoder would let a wrong base32 mapping cancel itself out and the vectors
    /// would still pass. Encoding independently is what makes the test above a check against RFC 6238
    /// rather than a check that the library agrees with itself.
    /// </remarks>
    private static string Base32OfSeed()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = Encoding.ASCII.GetBytes(RfcSeed);
        var output = new StringBuilder();
        int buffer = 0, bitsHeld = 0;

        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsHeld += 8;
            while (bitsHeld >= 5)
            {
                output.Append(alphabet[(buffer >> (bitsHeld - 5)) & 31]);
                bitsHeld -= 5;
            }
        }

        if (bitsHeld > 0)
        {
            output.Append(alphabet[(buffer << (5 - bitsHeld)) & 31]);
        }

        return output.ToString();
    }
}
