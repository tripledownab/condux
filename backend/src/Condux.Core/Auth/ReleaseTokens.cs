namespace Condux.Core.Auth;

/// <summary>
/// The per-project credential CI presents to record a release or upload a source map, with no cookie login.
///
/// Minting, hashing and shape-checking are <see cref="ScopedToken"/>'s; this type only names the prefix.
/// </summary>
public static class ReleaseTokens
{
    /// <summary>The stable prefix every token of this kind carries, for identification and secret scanning.</summary>
    public const string Prefix = "condux_rel_";

    /// <summary>Creates a fresh token: <c>Raw</c> is shown to the operator once, <c>Hash</c> is stored.</summary>
    public static (string Raw, string Hash) Create() => ScopedToken.Create(Prefix);

    /// <summary>Hashes a presented raw token for lookup against the store.</summary>
    public static string HashToken(string raw) => ScopedToken.Hash(raw);

    /// <summary>Whether a presented value is shaped like a token of this kind.</summary>
    public static bool LooksLikeToken(string? raw) => ScopedToken.LooksLike(raw, Prefix);
}
