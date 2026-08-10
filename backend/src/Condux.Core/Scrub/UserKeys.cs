using System.Security.Cryptography;
using System.Text;
using Condux.Core.Events;

namespace Condux.Core.Scrub;

/// <summary>
/// Derives the stable pseudonymous key behind "users affected" counts: a hash of the strongest user
/// identifier the SDK sent (id, else email, else username, else IP). Derived at ingest BEFORE the
/// scrubber redacts the email and drops the IP, so the raw identifiers never reach storage while the
/// same user still counts once across events.
/// </summary>
public static class UserKeys
{
    public static string Derive(EventUser? user)
    {
        var identifier = FirstNonEmpty(user?.Id, user?.Email?.ToLowerInvariant(), user?.Username, user?.IpAddress);
        if (identifier is null)
        {
            return "";
        }

        // 32 hex chars (128 bits) keeps the key compact; collisions are negligible at any realistic scale.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identifier)))[..32];
    }

    private static string? FirstNonEmpty(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c));
}
