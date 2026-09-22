namespace Condux.Core.Auth;

/// <summary>Email normalization + minimal shape validation for local accounts.</summary>
public static class Emails
{
    /// <summary>Canonical form used for storage and uniqueness: trimmed and lower-cased.</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    /// <summary>The lower-cased domain part of an email, or null when it isn't a valid single-'@' address.
    /// The routing key for enterprise SSO (you@acme.com -> acme.com -> Acme's IdP).</summary>
    public static string? Domain(string email) =>
        IsValid(email) ? email[(email.IndexOf('@') + 1)..].Trim().ToLowerInvariant() : null;

    /// <summary>
    /// A deliberately permissive sanity check — one <c>@</c> with non-empty local and domain parts,
    /// no whitespace, within RFC length. Permissive on purpose: an over-strict regex rejects valid
    /// addresses, and shape was never the interesting question about an address anyway.
    ///
    /// <para>What it does NOT establish is that the caller holds the address, and no local sign-up
    /// path establishes that either. So nothing downstream may treat the address on a local account
    /// as proof of who its owner is. An identity provider's assertion is different, and is the only
    /// thing here that carries that proof.</para>
    /// </summary>
    public static bool IsValid(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 320 || email.Contains(' '))
        {
            return false;
        }

        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 1 && email.IndexOf('@', at + 1) < 0;
    }
}
