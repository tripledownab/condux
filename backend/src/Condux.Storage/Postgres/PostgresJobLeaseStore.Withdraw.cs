namespace Condux.Storage.Postgres;

/// <summary>
/// When nothing holds a fix run: the lease condition the claim reads, and the withdrawal an admin makes of a
/// run in that state. Both run tables use both, so the rule is written once here, beside the claim.
/// </summary>
public sealed partial class PostgresJobLeaseStore
{
    // A lease nobody holds: never taken, or lapsed. Unqualified, since only the run table has the column, so
    // the claim's joins and a single-table update can share it.
    internal const string LeaseIsFree = "(lease_expires_at IS NULL OR lease_expires_at <= @now)";

    // What follows "UPDATE <run table>" to withdraw a run nothing holds: pending (1), or a runner's
    // (job_context set) whose lease is free by the claim's own condition, so a run free to lease again is
    // free to cancel. A free lease can neither heartbeat nor report, both guarded on lease_expires_at > now,
    // and clearing it here refuses even a report stamped before the lapse. That is the server's view only:
    // a runner whose heartbeats failed may still be working until its own check stops it, as when another
    // runner leases the run again. A hosted run that is running is never withdrawn, since the conductor is
    // executing it and the sweep owns its ending. Cancelled (5) rather than deleted, so a hosted job that
    // arrives later meets its run on the job id.
    internal const string WithdrawTail =
        " SET status = 5, lease_expires_at = NULL, updated_at = now() WHERE id = @id"
        + " AND (status = 1 OR (status = 2 AND job_context IS NOT NULL AND " + LeaseIsFree + "));";
}
