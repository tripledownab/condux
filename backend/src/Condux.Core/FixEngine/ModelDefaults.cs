namespace Condux.Core.FixEngine;

/// <summary>
/// The model a fix run uses when nothing else chooses one. Every path that enqueues a run needs this
/// answer, so it is stated once: the request endpoints, auto-fix, the CVE bump and a customer's runner
/// had each carried their own copy, which is how one of them quietly stays on last year's model.
///
/// An org with a BYO-key config overrides it per run; this is only the fallback.
/// </summary>
public static class ModelDefaults
{
    public const string Fix = "claude-opus-4-8";
}
