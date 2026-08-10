namespace Condux.Core.Auth;

/// <summary>
/// An immutable set of allowed emails parsed from a comma-separated config value, compared
/// case-insensitively via <see cref="Emails.Normalize"/>. Blank/null input yields an empty allowlist
/// (nothing allowed) — the safe default for a security gate that must be opted into explicitly.
/// </summary>
public sealed class EmailAllowlist
{
    private readonly HashSet<string> emails;

    public EmailAllowlist(string? commaSeparated) =>
        emails = (commaSeparated ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Emails.Normalize)
            .ToHashSet();

    /// <summary>Whether the given email is on the allowlist (normalized comparison).</summary>
    public bool Contains(string email) => emails.Contains(Emails.Normalize(email));

    /// <summary>How many emails are on the allowlist (0 = the gate is effectively closed).</summary>
    public int Count => emails.Count;
}
