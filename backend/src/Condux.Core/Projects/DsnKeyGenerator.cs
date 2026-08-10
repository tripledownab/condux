using System.Security.Cryptography;

namespace Condux.Core.Projects;

/// <summary>Generates DSN public keys — 32 lowercase hex chars, matching Sentry's key shape.</summary>
public static class DsnKeyGenerator
{
    public static string NewPublicKey()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }
}
