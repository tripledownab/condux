using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Condux.Core.Auth;

/// <summary>
/// The signed, short lived <c>state</c> handed to a third party at each leg of a "Connect" flow (GitHub,
/// Linear) and verified when it sends the browser back. It binds the redirect to the org the user chose and
/// the in app path to return to afterwards, with no server side pending state to store. HMAC-SHA256 over
/// every field the token carries, org id and expiry and return path and the installed flag, domain
/// separated by the flow's purpose and verified in constant time; an expired or tampered token yields null.
///
/// The state proves only which org minted it, and any org admin can mint one for their own org, so it is
/// half of a double submit rather than an authorization on its own. The caller that pairs it with a browser
/// cookie is the control plane's ConnectFlow.
/// </summary>
public static class ConnectState
{
    /// <summary>The GitHub connect flow. "connect" is what GitHub states were signed under before this
    /// type was shared, so a state minted by the previous build still verifies during a deploy.</summary>
    public const string GitHub = "connect";

    /// <summary>The Linear connect flow (ADR-0053).</summary>
    public const string Linear = "linear-connect";

    /// <summary>
    /// What a valid token carries: the org to act for, where to send the browser back, and whether the
    /// user has just come through the third party's install screen. GitHub needs that last flag to tell
    /// "you have not installed the app yet" from "an organization owner has to approve it", which look
    /// identical from here; a flow with no such step passes false.
    /// </summary>
    public sealed record Verified(long OrgId, string ReturnPath, bool Installed);

    public static string Create(
        string purpose, long orgId, string returnPath, bool installed, DateTimeOffset expiresAt, string secret)
    {
        var encodedPath = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(returnPath));
        var payload = $"{orgId}.{expiresAt.ToUnixTimeSeconds()}.{encodedPath}.{(installed ? '1' : '0')}";
        return $"{payload}.{Sign(purpose, payload, secret)}";
    }

    /// <summary>The org + return path the token was minted for, or null if malformed, tampered, expired or
    /// minted for another purpose.</summary>
    public static Verified? Validate(string purpose, string? token, DateTimeOffset now, string secret)
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

        var expected = Sign(purpose, $"{parts[0]}.{parts[1]}.{parts[2]}.{parts[3]}", secret);
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

    // Domain separated by purpose, so a state from one flow never verifies in another even under one
    // secret, and a connect token can never collide with a webhook body signature.
    private static string Sign(string purpose, string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{purpose}:{payload}")));
    }
}
