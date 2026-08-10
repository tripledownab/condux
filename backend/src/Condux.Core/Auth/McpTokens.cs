using System.Security.Cryptography;
using System.Text;

namespace Condux.Core.Auth;

/// <summary>
/// Scoped MCP tokens: a per-project machine credential so an AI agent (Claude, Cursor, ...) can read a
/// project's issues over the MCP endpoint (<c>POST /api/mcp</c>, <c>Authorization: Bearer</c>) without a
/// cookie login. Read-only, scoped to one project. The raw token is a prefixed 256-bit random value shown
/// to the operator once; only its SHA-256 hash is persisted, so a leak of the table can't be replayed. The
/// <see cref="Prefix"/> makes the token identifiable and secret-scannable. Mirrors <see cref="ReleaseTokens"/>.
/// </summary>
public static class McpTokens
{
    /// <summary>The stable prefix every MCP token carries (for identification + secret scanning).</summary>
    public const string Prefix = "condux_mcp_";
    private const int TokenBytes = 32;

    /// <summary>Creates a fresh token: <c>Raw</c> is shown to the operator once, <c>Hash</c> is stored.</summary>
    public static (string Raw, string Hash) Create()
    {
        var raw = Prefix + Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        return (raw, HashToken(raw));
    }

    /// <summary>Hashes a presented raw token (from an Authorization header) for lookup against the store.</summary>
    public static string HashToken(string raw)
    {
        ArgumentException.ThrowIfNullOrEmpty(raw);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
