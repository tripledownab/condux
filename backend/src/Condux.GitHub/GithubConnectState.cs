using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Condux.GitHub;

/// <summary>
/// The signed, short-lived <c>state</c> we hand to GitHub when a user starts a "Connect GitHub" flow, and
/// verify when GitHub redirects back to the Setup URL. It binds the redirect to the org the user chose and
/// the in-app path to return to afterwards, with no server-side pending-state to store. HMAC-SHA256 over
/// the org id + expiry + return path (domain-separated), verified in constant time; an expired or tampered
/// token yields null.
/// </summary>
public static class GithubConnectState
{
    /// <summary>What a valid token carries: the org to tie the install to and where to send the browser back.</summary>
    public sealed record Verified(long OrgId, string ReturnPath);

    public static string Create(long orgId, string returnPath, DateTimeOffset expiresAt, string secret)
    {
        var encodedPath = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(returnPath));
        var payload = $"{orgId}.{expiresAt.ToUnixTimeSeconds()}.{encodedPath}";
        return $"{payload}.{Sign(payload, secret)}";
    }

    /// <summary>The org + return path the token was minted for, or null if malformed, tampered, or expired.</summary>
    public static Verified? Validate(string? token, DateTimeOffset now, string secret)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 4
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var orgId)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiryUnix))
        {
            return null;
        }

        var expected = Sign($"{parts[0]}.{parts[1]}.{parts[2]}", secret);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[3])))
        {
            return null;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) <= now)
        {
            return null;
        }

        // The return path was covered by the signature above, so decoding it here is safe.
        return new Verified(orgId, Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[2])));
    }

    // Domain-separated ("connect:") so a connect token can never collide with a webhook body signature.
    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes("connect:" + payload)));
    }
}
