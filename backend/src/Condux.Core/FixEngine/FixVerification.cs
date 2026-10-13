namespace Condux.Core.FixEngine;

/// <summary>Post-merge verification state of a fix run (ADR-0019) or a CVE bump (ADR-0051). Values
/// persist in <c>fix_suggestions.verify_status</c> and <c>cve_fix_runs.verify_status</c> and ride the
/// wire.</summary>
public enum VerifyStatus
{
    None = 0,
    Watching = 1,
    Held = 2,
    /// <summary>An issue fix whose issue recurred, or a CVE bump still exposed at its deadline.</summary>
    DidNotHold = 3,
    /// <summary>A CVE bump the inventory could not settle either way. Issue fixes never take it.</summary>
    CannotVerify = 4,
}

/// <summary>The verification policy, pure and clock-free: a merged fix holds when the issue stays
/// silent for the whole window, fails as soon as any occurrence lands, and is watched meanwhile.
/// V1 watches from the merge time; gating on the next release is a refinement.</summary>
public static class FixVerification
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(72);

    public static VerifyStatus Evaluate(
        DateTimeOffset mergedAt, DateTimeOffset now, long occurrencesSinceMerge) =>
        occurrencesSinceMerge > 0
            ? VerifyStatus.DidNotHold
            : now - mergedAt >= Window
                ? VerifyStatus.Held
                : VerifyStatus.Watching;
}
