using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Condux.Core.Auth;

/// <summary>
/// Password hashing with <b>Argon2id</b> — OWASP's first-choice algorithm for password storage
/// (memory-hard, GPU/ASIC-resistant) — via the vetted, MIT-licensed
/// <c>Konscious.Security.Cryptography</c> library rather than a hand-rolled implementation.
/// Output is the standard PHC string <c>$argon2id$v=19$m=&lt;kib&gt;,t=&lt;iters&gt;,p=&lt;par&gt;$&lt;saltB64&gt;$&lt;hashB64&gt;</c>,
/// which carries its own parameters so they can be tuned later without invalidating stored hashes.
/// </summary>
public static class PasswordHasher
{
    // OWASP-recommended Argon2id parameters (19 MiB, 2 iterations, 1 lane).
    private const int MemoryKib = 19_456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Argon2Version = 19; // 0x13

    /// <summary>Hashes a password into an encoded PHC string that is safe to persist as-is.</summary>
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, MemoryKib, Iterations, Parallelism, HashBytes);
        return $"$argon2id$v={Argon2Version}$m={MemoryKib},t={Iterations},p={Parallelism}"
            + $"${Base64NoPad(salt)}${Base64NoPad(hash)}";
    }

    /// <summary>
    /// Verifies a password against an encoded hash in constant time, using the parameters embedded
    /// in the hash. Returns false on any mismatch or malformed input (never throws).
    /// </summary>
    public static bool Verify(string password, string encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(encoded) || !TryParse(encoded, out var p))
        {
            return false;
        }

        var actual = Derive(password, p.Salt, p.MemoryKib, p.Iterations, p.Parallelism, p.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, p.Hash);
    }

    private static byte[] Derive(string password, byte[] salt, int memoryKib, int iterations, int parallelism, int length)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(length);
    }

    private readonly record struct Params(int MemoryKib, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);

    private static bool TryParse(string encoded, out Params parsed)
    {
        parsed = default;
        // $argon2id$v=19$m=<kib>,t=<iters>,p=<par>$<saltB64>$<hashB64>
        var parts = encoded.Split('$');
        if (parts.Length != 6 || parts[0].Length != 0 || parts[1] != "argon2id")
        {
            return false;
        }

        if (!TryParseTagged(parts[2], "v=", out var version) || version != Argon2Version)
        {
            return false;
        }

        var cost = parts[3].Split(',');
        if (cost.Length != 3
            || !TryParseTagged(cost[0], "m=", out var memoryKib)
            || !TryParseTagged(cost[1], "t=", out var iterations)
            || !TryParseTagged(cost[2], "p=", out var parallelism))
        {
            return false;
        }

        try
        {
            parsed = new Params(memoryKib, iterations, parallelism, FromBase64NoPad(parts[4]), FromBase64NoPad(parts[5]));
            return parsed.Salt.Length > 0 && parsed.Hash.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseTagged(string field, string tag, out int value)
    {
        value = 0;
        return field.StartsWith(tag, StringComparison.Ordinal)
            && int.TryParse(field.AsSpan(tag.Length), NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static string Base64NoPad(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromBase64NoPad(string value) =>
        Convert.FromBase64String(value.PadRight((value.Length + 3) / 4 * 4, '='));
}
