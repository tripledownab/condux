using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Condux.Core.Auth;

/// <summary>
/// The signed, short-lived token carried by the <c>condux_impersonation</c> cookie when a platform admin
/// starts a read-only "view as org" session (ADR-0027). It binds the session to BOTH the admin's user id
/// and the target org, so it grants scope only when replayed with that same admin's login: a leaked
/// cookie is useless on its own, and it can never widen to a different admin or a different org without
/// re-minting (which is gated by the signing key). HMAC-SHA256 over the payload, verified in constant
/// time; a malformed, tampered, expired, or wrong-admin token yields null. Mirrors
/// <c>Condux.GitHub.GithubConnectState</c>, domain-separated ("impersonate:") so the two can never collide.
/// </summary>
public static class ImpersonationToken
{
    public static string Create(long adminUserId, long targetOrgId, DateTimeOffset expiresAt, string secret)
    {
        var payload = $"{adminUserId}.{targetOrgId}.{expiresAt.ToUnixTimeSeconds()}";
        return $"{payload}.{Sign(payload, secret)}";
    }

    /// <summary>The target org id the token was minted for, or null if it is malformed, tampered, expired,
    /// or was minted for a different admin than the one presenting it.</summary>
    public static long? Validate(string? token, long currentAdminUserId, DateTimeOffset now, string secret)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length != 4
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var adminUserId)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var orgId)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expiryUnix))
        {
            return null;
        }

        var expected = Sign($"{parts[0]}.{parts[1]}.{parts[2]}", secret);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[3])))
        {
            return null;
        }

        return adminUserId == currentAdminUserId && DateTimeOffset.FromUnixTimeSeconds(expiryUnix) > now
            ? orgId
            : null;
    }

    // Domain-separated ("impersonate:") so this token can never collide with the github connect-state
    // token ("connect:") even under the same signing secret.
    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes("impersonate:" + payload)));
    }
}
