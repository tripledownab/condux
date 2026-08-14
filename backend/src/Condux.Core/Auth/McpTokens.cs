namespace Condux.Core.Auth;

/// <summary>
/// The read-only credential an MCP client presents to reach one project's issues (ADR-0029).
///
/// Minting, hashing and shape-checking are <see cref="ScopedToken"/>'s; this type only names the prefix.
/// </summary>
public static class McpTokens
{
    /// <summary>The stable prefix every token of this kind carries, for identification and secret scanning.</summary>
    public const string Prefix = "condux_mcp_";

    /// <summary>Creates a fresh token: <c>Raw</c> is shown to the operator once, <c>Hash</c> is stored.</summary>
    public static (string Raw, string Hash) Create() => ScopedToken.Create(Prefix);

    /// <summary>Hashes a presented raw token for lookup against the store.</summary>
    public static string HashToken(string raw) => ScopedToken.Hash(raw);

    /// <summary>Whether a presented value is shaped like a token of this kind.</summary>
    public static bool LooksLikeToken(string? raw) => ScopedToken.LooksLike(raw, Prefix);
}
