using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Condux.GitHub;

/// <summary>
/// The signed, short-lived <c>state</c> we hand to GitHub at each leg of a "Connect GitHub" flow, and
/// verify when GitHub sends the browser back, whether to the Setup URL or the OAuth callback. It binds the
/// redirect to the org the user chose and the in-app path to return to afterwards, with no server-side
/// pending-state to store. HMAC-SHA256 over every field the token carries, org id and expiry and return
/// path and the installed flag, domain-separated and verified in constant time; an expired or tampered
/// token yields null.
///
/// The state proves only which org minted it, and any org admin can mint one for their own org, so it is
/// half of a double submit rather than an authorization on its own. The caller that pairs it with a
/// browser cookie is the control-plane's GithubConnectFlow.
/// </summary>
public static class GithubConnectState
{
    /// <summary>
    /// What a valid token carries: the org to tie the install to, where to send the browser back, and
    /// whether the user has just come through GitHub's install screen. That last flag is what separates
    /// "you have not installed the app yet" from "you installed it and an organization owner has to
    /// approve it": both look identical from here, an empty list of reachable installations, and treating
    /// the second as the first sends the user back to install in a loop.
    /// </summary>
    public sealed record Verified(long OrgId, string ReturnPath, bool Installed);

    public static string Create(
        long orgId, string returnPath, bool installed, DateTimeOffset expiresAt, string secret)
    {
        var encodedPath = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(returnPath));
        var payload = $"{orgId}.{expiresAt.ToUnixTimeSeconds()}.{encodedPath}.{(installed ? '1' : '0')}";
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
        if (parts.Length != 5
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var orgId)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expiryUnix)
            || parts[3] is not ("0" or "1"))
        {
            return null;
        }

        var expected = Sign($"{parts[0]}.{parts[1]}.{parts[2]}.{parts[3]}", secret);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[4])))
        {
            return null;
        }

        if (DateTimeOffset.FromUnixTimeSeconds(expiryUnix) <= now)
        {
            return null;
        }

        // The return path and the flag were covered by the signature above, so reading them here is safe.
        return new Verified(
            orgId, Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[2])), parts[3] == "1");
    }

    // Domain-separated ("connect:") so a connect token can never collide with a webhook body signature.
    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes("connect:" + payload)));
    }
}
