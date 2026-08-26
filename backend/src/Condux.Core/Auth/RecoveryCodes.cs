using System.Security.Cryptography;

namespace Condux.Core.Auth;

/// <summary>
/// Single-use codes that stand in for the authenticator when the device is lost. Without them, losing a
/// phone means losing the account, and the support path for that is an operator disabling MFA on a
/// request they cannot verify, which is a worse hole than the one MFA closed.
/// </summary>
public static class RecoveryCodes
{
    public const int Count = 10;

    /// <summary>
    /// Ten codes: the raw values, shown once and never recoverable, and the hashes to store.
    /// </summary>
    /// <remarks>
    /// Grouped as <c>xxxxx-xxxxx</c> because these get written down and typed back in under stress.
    /// Crockford's alphabet, so the pairs a reader confuses (0/O, 1/I/L, U) are never both in it. 50 bits
    /// each, far past guessable.
    ///
    /// Hashed with <see cref="SessionTokens.HashToken"/> rather than Argon2id, and rather than a hashing
    /// helper of its own. Plain SHA-256 is right because these are high-entropy random values, not
    /// passwords: a work factor buys nothing against a 50-bit search and costs latency on every attempt.
    /// Reusing the session helper is right because this codebase has already been bitten by copied
    /// credential hashing drifting into two encodings and two casings.
    /// </remarks>
    public static (string[] Raw, string[] Hashes) Generate()
    {
        var raw = new string[Count];
        var hashes = new string[Count];
        for (var i = 0; i < Count; i++)
        {
            raw[i] = $"{Group()}-{Group()}";
            hashes[i] = SessionTokens.HashToken(Normalize(raw[i]));
        }

        return (raw, hashes);
    }

    /// <summary>
    /// Folds the formatting and the transcription slips a human adds, so a code matches on its value
    /// rather than on how it was typed. Applied on both sides, at generation and at redemption, or
    /// nothing would ever match.
    /// </summary>
    /// <remarks>
    /// The character folding is the half that is easy to leave out. Excluding I, L, O and U from the
    /// alphabet means we never GENERATE an ambiguous character, but it does nothing for a user who reads
    /// their own handwritten 0 as an O and types that. Crockford's encoding folds those on decode for
    /// exactly this reason, and omitting it breaks precisely the written-down-and-retyped case the
    /// format exists to serve.
    /// </remarks>
    public static string Normalize(string code)
    {
        var trimmed = code.Trim().ToUpperInvariant();
        return string.Create(CountKept(trimmed), trimmed, static (span, source) =>
        {
            var next = 0;
            foreach (var character in source)
            {
                if (character is '-' or ' ')
                {
                    continue;
                }

                span[next++] = character switch
                {
                    'O' => '0',
                    'I' or 'L' => '1',
                    'U' => 'V', // Crockford treats U as an error; V is the character it is mistaken for
                    _ => character,
                };
            }
        });
    }

    private static int CountKept(string value)
    {
        var kept = 0;
        foreach (var character in value)
        {
            if (character is not ('-' or ' '))
            {
                kept++;
            }
        }

        return kept;
    }

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static string Group() =>
        string.Create(5, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
}
