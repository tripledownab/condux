using System.Security.Cryptography;
using System.Text;

namespace Condux.Core.Auth;

/// <summary>
/// How every machine credential in Condux is minted and checked: a stable prefix, a random secret shown
/// once, and only its SHA-256 stored, so a leaked database is not a set of live credentials.
///
/// One implementation rather than one per token type. Three copies had already drifted into two different
/// secret encodings and two different hash casings, which stays invisible until something compares hashes
/// across types and is then subtly wrong. The prefix is the only thing a token type gets to choose.
///
/// The format is fixed by tokens already issued: base64url secret, upper-case hex hash. Changing either
/// would invalidate every stored hash, so it is pinned by tests rather than left to a future tidy-up.
/// </summary>
public static class ScopedToken
{
    private const int SecretBytes = 32;

    /// <summary>The secret's length once encoded, so a token type can validate shape before hashing.</summary>
    public static int EncodedSecretLength { get; } = Base64Url(new byte[SecretBytes]).Length;

    /// <summary>Mints a token: <c>Raw</c> is shown to the operator once, <c>Hash</c> is what gets stored.</summary>
    public static (string Raw, string Hash) Create(string prefix)
    {
        var raw = prefix + Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        return (raw, Hash(raw));
    }

    /// <summary>Hashes a presented raw token for lookup against the store.</summary>
    public static string Hash(string raw)
    {
        ArgumentException.ThrowIfNullOrEmpty(raw);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>
    /// Whether a value is even shaped like a token of this kind. Cheap rejection before hashing and a
    /// database round trip, and it keeps a token of one kind presented to another's endpoint from being
    /// looked up at all.
    /// </summary>
    public static bool LooksLike(string? raw, string prefix) =>
        raw is not null
        && raw.StartsWith(prefix, StringComparison.Ordinal)
        && raw.Length == prefix.Length + EncodedSecretLength;

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
