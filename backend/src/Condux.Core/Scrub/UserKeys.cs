using System.Security.Cryptography;
using System.Text;
using Condux.Core.Events;

namespace Condux.Core.Scrub;

/// <summary>
/// Derives the stable pseudonymous key behind "users affected" counts: a keyed hash of the strongest user
/// identifier the SDK sent (id, else email, else username, else IP). Derived at ingest BEFORE the
/// scrubber redacts the email and drops the IP, so the raw identifiers never reach storage while the
/// same person still counts once across events.
///
/// <para><b>The project's salt is the key, and it is what makes the pseudonym a pseudonym.</b> This was a
/// bare SHA-256, which does not survive the identifiers it is built from: an email comes from a list, a
/// username from a smaller one, and an IPv4 address from a space of 2^32, so hashing an address is not
/// the same as dropping it. The stored value is not only in the database either. It is serialised into
/// the event payload the dashboard reads, so any member of the project held it without a database at
/// all, and reversing it undid the drop the scrubber performs one line later.</para>
///
/// <para>Per project rather than one key for the deployment, because anyone can sign up and own an org.
/// With a single key, a stranger could submit events carrying identifiers they chose, read the derived
/// keys back and build a table that re-identifies any other project's users. A salt nobody else holds
/// makes that table worth nothing outside the project that built it. The relay already resolves and
/// caches the project on every ingest, so carrying the salt there costs no extra round trip.</para>
/// </summary>
public static class UserKeys
{
    /// <param name="salt">The project's <c>user_key_salt</c>. Required, and never empty: an empty key
    /// still produces a deterministic HMAC, which is the defect this exists to close rather than a
    /// degraded form of it.</param>
    public static string Derive(EventUser? user, string salt)
    {
        var identifier = FirstNonEmpty(user?.Id, user?.Email?.ToLowerInvariant(), user?.Username, user?.IpAddress);
        if (identifier is null)
        {
            // Asked before the salt, because an event carrying no user at all is ordinary and must not
            // depend on configuration to be accepted.
            return "";
        }

        if (salt.Length == 0)
        {
            // Unreachable through the ingest path: the column is NOT NULL and the value is a required
            // parameter on Project, so the compiler and the schema both demand one. Loud rather than
            // silent all the same, since the quiet alternative is the weak hash this replaced.
            throw new ArgumentException("A project's user-key salt is required and must not be empty.", nameof(salt));
        }

        // 32 hex chars (128 bits) keeps the key compact; collisions are negligible at any realistic scale.
        return Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(identifier)))[..32];
    }

    /// <summary>
    /// A fresh salt for one project. The one place a salt is minted, so the length cannot drift between
    /// the store that creates a project and the dev store that fakes one.
    ///
    /// <para>256 bits because this is an HMAC key and not an identifier: it is never displayed, never
    /// compared and never typed, so there is nothing to trade its length against.</para>
    /// </summary>
    public static string NewSalt() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c));
}
