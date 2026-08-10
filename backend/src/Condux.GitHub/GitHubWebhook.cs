using System.Security.Cryptography;
using System.Text;

namespace Condux.GitHub;

/// <summary>Verifies inbound GitHub webhook deliveries against the app's webhook secret.</summary>
public static class GitHubWebhook
{
    /// <summary>
    /// True when <paramref name="signatureHeader"/> (the <c>X-Hub-Signature-256</c> value, e.g.
    /// <c>sha256=ab12...</c>) is a valid HMAC-SHA256 of <paramref name="body"/> under
    /// <paramref name="secret"/>. Uses a constant-time comparison so a timing side channel can't leak the
    /// expected signature.
    /// </summary>
    public static bool IsValidSignature(string secret, byte[] body, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader)
            || !signatureHeader.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var expected = "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(body));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signatureHeader));
    }
}
