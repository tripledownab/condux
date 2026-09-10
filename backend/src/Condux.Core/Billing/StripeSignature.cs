using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Condux.Core.Billing;

/// <summary>
/// Verifies an inbound Stripe webhook against the endpoint's signing secret. Stripe signs the raw request
/// body: the <c>Stripe-Signature</c> header carries a timestamp <c>t</c> and one or more <c>v1</c> HMAC-
/// SHA256 hex signatures over <c>"{t}.{payload}"</c>. This checks the signature (constant-time) and that
/// the timestamp is within a tolerance so a captured request can't be replayed later. Pure and
/// dependency-free (no Stripe SDK), so the whole check is unit-tested in CI, as the GitHub
/// webhook HMAC is hand-rolled in Condux.GitHub).
/// </summary>
public static class StripeSignature
{
    // Stripe's own libraries default to a 5-minute replay window.
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when <paramref name="signatureHeader"/> (the <c>Stripe-Signature</c> value) contains a valid
    /// <c>v1</c> HMAC-SHA256 of <c>"{t}.{payload}"</c> under <paramref name="secret"/> and the timestamp
    /// <c>t</c> is within <paramref name="tolerance"/> of <paramref name="now"/>. False on any malformed
    /// header, bad signature, or stale timestamp.
    /// </summary>
    public static bool Verify(
        string payload, string? signatureHeader, string secret, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrEmpty(signatureHeader) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in signatureHeader.Split(',', StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = part[..eq];
            var value = part[(eq + 1)..];
            if (key == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
            }
            else if (key == "v1")
            {
                signatures.Add(value);
            }
        }

        if (timestamp is not { } ts || signatures.Count == 0)
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(ts);
        if (age.Duration() > (tolerance ?? DefaultTolerance))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = Convert.ToHexStringLower(
            hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.{payload}")));
        var expectedBytes = Encoding.ASCII.GetBytes(expected);

        // Any matching v1 signature is a pass (Stripe may send several during a secret rotation).
        foreach (var candidate in signatures)
        {
            var candidateBytes = Encoding.ASCII.GetBytes(candidate);
            if (candidateBytes.Length == expectedBytes.Length
                && CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes))
            {
                return true;
            }
        }

        return false;
    }
}
