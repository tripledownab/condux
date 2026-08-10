using System.Security.Cryptography;
using System.Text;

namespace Condux.Core.Auth;

/// <summary>
/// Opaque session tokens. The raw token is a 256-bit random value (URL-safe base64) handed to the
/// client in a cookie; only its SHA-256 hash is persisted, so a leak of the sessions table can't be
/// replayed to authenticate. Lookups hash the presented token and match on the stored hash.
/// </summary>
public static class SessionTokens
{
    private const int TokenBytes = 32;

    /// <summary>
    /// Creates a fresh session token: <c>Raw</c> goes to the client (cookie), <c>Hash</c> is stored.
    /// </summary>
    public static (string Raw, string Hash) Create()
    {
        var raw = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        return (raw, HashToken(raw));
    }

    /// <summary>Hashes a presented raw token (from a cookie) for lookup against the stored hash.</summary>
    public static string HashToken(string raw)
    {
        ArgumentException.ThrowIfNullOrEmpty(raw);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
