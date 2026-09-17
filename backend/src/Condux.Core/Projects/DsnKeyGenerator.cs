using System.Security.Cryptography;

namespace Condux.Core.Projects;

/// <summary>Generates DSN public keys (32 lowercase hex chars, matching Sentry's key shape) and
/// answers whether a value has that shape. Both live here so the shape is stated once: every stored
/// key came from <see cref="NewPublicKey"/>, so a value <see cref="IsWellFormedPublicKey"/> refuses
/// cannot match a row, and the relay can say so without asking Postgres.</summary>
public static class DsnKeyGenerator
{
    private const int KeyBytes = 16;

    // Derived, so widening the key cannot leave the check behind on the old length.
    private const int KeyLength = KeyBytes * 2;

    public static string NewPublicKey()
    {
        Span<byte> bytes = stackalloc byte[KeyBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public static bool IsWellFormedPublicKey(string publicKey)
    {
        if (publicKey.Length != KeyLength)
        {
            return false;
        }

        foreach (var c in publicKey)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }
}
