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
    /// <summary>
    /// What replaces a redacted value. Public because a reader downstream has to be able to recognise
    /// it: a scrubbed value is not data, and code that would otherwise store or match on it needs to
    /// compare against the same constant rather than restate the literal and drift from it.
    /// </summary>
    public const string Redacted = "[redacted]";

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    // Token-shaped secrets: sk-/pk-/ghp-/gho-/xox?-/AKIA... and similar.
    //
    // These patterns stay NARROW on purpose, and the limit is worth stating because it looks like an
    // oversight. They match shapes that are unambiguously credentials. They do not match a bare
    // "name=value" pair, so a session cookie sitting inside free text (a log line, or a captured frame
    // local holding a whole request object) survives this pass.
    //
    // Widening it to catch those would mean guessing at arbitrary text, and a scrub that redacts real
    // data is a scrub people turn off, which costs more than the cases it would catch. The controls that
    // actually carry this weight are structural and sit earlier: IsSensitiveKey drops a value outright by
    // its key, and the SDKs do not put headers on an event at all, so credentials mostly never arrive
    // here to be matched. Reviewed and kept narrow deliberately, 2026-08-15.
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
