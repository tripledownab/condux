using System.Text.RegularExpressions;

namespace Condux.Core.Scrub;

/// <summary>
/// PII / secret scrubbing shared by the relay (inbound) and the control-plane
/// (before assembling context for the Conductor). Defense in depth: scrub on
/// ingest, and again when building a fix prompt, so secrets never reach storage
/// or a model.
/// </summary>
public static partial class Scrubber
{
    private const string Redacted = "[redacted]";

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    // Token-shaped secrets: sk-/pk-/ghp-/gho-/xox?-/AKIA... and similar.
    [GeneratedRegex(@"\b(?:sk|pk|ghp|gho|xox[baprs]|AKIA)[-_A-Za-z0-9]{10,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    /// <summary>Redact emails and token-shaped secrets from free text.</summary>
    public static string ScrubString(string input)
    {
        var s = EmailRegex().Replace(input, Redacted);
        return TokenRegex().Replace(s, Redacted);
    }

    /// <summary>
    /// Whether a key's value should always be dropped regardless of its shape
    /// (e.g. headers/tags named authorization, password, api-key).
    /// </summary>
    public static bool IsSensitiveKey(string key)
    {
        var k = key.ToLowerInvariant().Replace("-", "").Replace("_", "");
        return k is "authorization" or "password" or "passwd" or "secret"
            or "token" or "apikey" or "cookie" or "setcookie"
            || k.Contains("secret") || k.Contains("token")
            || k.Contains("password") || k.Contains("apikey");
    }
}
