namespace Condux.Core.FixEngine;

/// <summary>
/// When a leased job may be handed to another runner (ADR-0033 slice 4). A customer-hosted runner can
/// lose power, lose its network or be killed mid-run, and nothing we control notices. A lease with an
/// expiry means the work returns to the queue on its own rather than a fix sitting Running forever
/// because the machine that claimed it never came back.
///
/// The runner extends its lease while it works, so the expiry measures silence rather than duration: a
/// long, healthy run keeps its claim, and a short run that dies loses one.
///
/// Only the policy lives here. Deciding whether a job may be claimed has to happen inside the claim
/// itself, as one guarded UPDATE ... RETURNING, or two runners racing both read "free" and both take the
/// same job. A predicate here that restated that condition would be a second implementation of it, free
/// to drift from the one that actually runs, so the rules are asserted against the repository instead.
/// </summary>
public static class JobLease
{
    /// <summary>
    /// How long a claim survives without a heartbeat. Long enough to ride out a restart or a slow model
    /// call, short enough that a dead runner does not strand a fix for an afternoon.
    /// </summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(5);

    /// <summary>The expiry to stamp when a job is claimed or a heartbeat lands.</summary>
    public static DateTimeOffset ExpiresAt(DateTimeOffset now) => now + Duration;
}

/// <summary>
/// Which table a leased job lives in. A runner never sees this — the work it is handed has one shape
/// either way — but the server needs it after the claim: an issue fix gets a fix_audit trail and a CVE
/// bump does not (its run row is the whole record, ADR-0023), and the two report into different tables.
/// </summary>
public enum JobKind
{
    IssueFix = 1,
    CveFix = 2,
}

/// <summary>
/// What a runner needs to execute a fix without asking us anything else: the branch the draft pull request
/// targets, the scoped and double-scrubbed prompt, and the files the fix should focus on.
///
/// Stored on the run and handed over at claim time. Its presence is also what marks a run as a runner's to
/// take, so a fix the hosted Conductor is executing can never be claimed out from under it.
/// </summary>
public sealed record RunnerJobContext(string BaseBranch, string Prompt, IReadOnlyList<string> ScopedPaths)
{
    /// <summary>What a job that carries no usable context reads as. A runner refuses this rather than
    /// inventing a prompt: a fix run that read nothing produces a confident wrong patch, and the customer
    /// pays for the model call either way.</summary>
    public static RunnerJobContext None { get; } = new("", "", []);
}
