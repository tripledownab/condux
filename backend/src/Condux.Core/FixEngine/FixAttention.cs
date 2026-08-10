namespace Condux.Core.FixEngine;

/// <summary>Whether a fix run has reached a state a human would act on — the "needs attention" rule
/// behind the Fixes badge (#118). Pure so it is single-sourced and unit-tested.</summary>
public static class FixAttention
{
    /// <summary>True once the run wants review: a draft PR is ready (succeeded, not yet merged), the
    /// run failed, or verification concluded. Pending/running and mid-verification (watching) are
    /// still in progress, so they never demand attention.</summary>
    public static bool NeedsAttention(FixStatus status, VerifyStatus verify) =>
        verify is VerifyStatus.Held or VerifyStatus.DidNotHold
        || status is FixStatus.Failed
        || (status is FixStatus.Succeeded && verify is VerifyStatus.None);
}
