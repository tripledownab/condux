namespace Condux.Core.Auth;

/// <summary>
/// The credential a customer-hosted runner presents to lease work and report results (ADR-0033 slice 4). Its own type rather than a reused MCP or release token: a runner acts for a whole org, which is a different blast radius from reading one project, and revoking one capability should not take out the other.
///
/// Minting, hashing and shape-checking are <see cref="ScopedToken"/>'s; this type only names the prefix.
/// </summary>
public static class RunnerTokens
{
    /// <summary>The stable prefix every token of this kind carries, for identification and secret scanning.</summary>
    public const string Prefix = "condux_run_";

    /// <summary>Creates a fresh token: <c>Raw</c> is shown to the operator once, <c>Hash</c> is stored.</summary>
    public static (string Raw, string Hash) Create() => ScopedToken.Create(Prefix);

    /// <summary>Hashes a presented raw token for lookup against the store.</summary>
    public static string HashToken(string raw) => ScopedToken.Hash(raw);

    /// <summary>Whether a presented value is shaped like a token of this kind.</summary>
    public static bool LooksLikeToken(string? raw) => ScopedToken.LooksLike(raw, Prefix);
}
