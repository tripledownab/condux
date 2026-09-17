using Condux.Core.Auth;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// The domain-verification half of the SSO registry (ADR-0043): proving a claim, re-checking it on a
/// timer, and losing it again. Separate from the config CRUD because it is a lifecycle with its own rules,
/// not because the other file grew.
///
/// Every write here is a guarded UPDATE whose result is the answer, and that is the whole concurrency
/// design. Several control-plane replicas run the re-check, so a read-then-write would let two of them
/// decide the same transition and send the org the same notice twice. A statement that reports whether IT
/// made the change cannot.
/// </summary>
public sealed partial class PostgresSsoConfigStore
{
    // Guarded on the domain as well as the org: the token the admin published belongs to one claim, so a
    // config edited between the check starting and this landing must not stamp the new domain as proved.
    private const string MarkVerifiedSql = """
        UPDATE sso_configs SET verified_at = now(), verification_lost_at = NULL
        WHERE org_id = @org AND email_domain = @domain
        RETURNING verified_at;
        """;

    // Picks one verified row due for a re-check and stamps it in the same statement, so the stamp IS the
    // claim. FOR UPDATE SKIP LOCKED is what lets every replica run this loop: each gets its own row or
    // nothing, never the same one. The same shape as PostgresJobLeaseStore's claim.
    //
    // Stamped before the lookup runs, deliberately. A process that dies mid-check leaves the row stamped
    // and skips one day, which costs nothing; stamping afterwards would let a crash re-read the same
    // domain on every tick for ever.
    //
    // NULLS FIRST puts the never-checked rows at the front, which is every row proved before this shipped.
    //
    // The stamp and the cutoff come from ONE instant, passed in. They used to be now() and a parameter,
    // which is two clocks for one decision: a row stamped by the database can still sit behind a cutoff
    // the caller computed, so the same row is handed out again inside the same pass. Measured, and it ran
    // until the caller's own per-pass ceiling stopped it.
    private const string ClaimDueSql = $"""
        UPDATE sso_configs SET verification_checked_at = @now
        WHERE org_id = (
            SELECT org_id FROM sso_configs
            WHERE verified_at IS NOT NULL
              AND (verification_checked_at IS NULL OR verification_checked_at < @due)
            ORDER BY verification_checked_at NULLS FIRST
            LIMIT 1
            FOR UPDATE SKIP LOCKED)
        RETURNING {Columns};
        """;

    // The first failing check, and only the first: guarded on verification_lost_at being absent, so the
    // row count says whether THIS call started the grace period and therefore owes the org a notice.
    // Guarded on verified_at too, because a claim the org deleted or re-saved meanwhile is not failing.
    //
    // Stamped from the pass's own instant, not now(), for the reason the claim above gives: LapseSql
    // compares this column against a cutoff the caller computed, and a column written by one clock and
    // read against another makes the grace period depend on the skew between them.
    private const string RecordFailureSql = """
        UPDATE sso_configs SET verification_lost_at = @now
        WHERE org_id = @org AND email_domain = @domain
          AND verified_at IS NOT NULL AND verification_lost_at IS NULL
        RETURNING verification_lost_at;
        """;

    // The record came back inside the grace period. No notice: the org was told a date, that date passed
    // without the outage, and a second message saying so is noise.
    private const string ClearFailureSql = """
        UPDATE sso_configs SET verification_lost_at = NULL
        WHERE org_id = @org AND email_domain = @domain AND verification_lost_at IS NOT NULL;
        """;

    // The grace period ran out. verified_at goes to NULL, which stops the claim routing AND releases the
    // domain from the partial unique index, so the org that actually holds it can now prove it.
    //
    // verification_lost_at is left set on purpose: it is the only record of when this happened, and the
    // dashboard and the notice both name that date. MarkVerifiedSql above clears it on a fresh proof.
    private const string LapseSql = """
        UPDATE sso_configs SET verified_at = NULL
        WHERE org_id = @org AND email_domain = @domain
          AND verified_at IS NOT NULL
          AND verification_lost_at IS NOT NULL AND verification_lost_at <= @cutoff
        RETURNING verification_lost_at;
        """;

    /// <summary>
    /// Records that the org proved control of this exact domain, and returns the instant stamped. Null
    /// when the config moved on and no row matched.
    ///
    /// The statement returns the instant so the caller needs no read after it. A read sequenced after a
    /// committed write turns its own failure into an error for work that already happened.
    /// </summary>
    /// <exception cref="DomainAlreadyVerifiedException">Another org proved the same domain first. The
    /// partial unique index decides that, not a read beforehand, so two orgs racing on one domain cannot
    /// both win it.</exception>
    public async Task<DateTimeOffset?> MarkVerifiedAsync(
        long orgId, string emailDomain, CancellationToken cancellationToken = default)
    {
        try
        {
            return await TimestampAsync(
                MarkVerifiedSql,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("org", orgId);
                    cmd.Parameters.AddWithValue("domain", emailDomain);
                },
                cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new DomainAlreadyVerifiedException(emailDomain, ex);
        }
    }

    /// <summary>
    /// Takes the next verified claim last checked more than <paramref name="recheckAfter"/> before
    /// <paramref name="now"/>, or null when none is. Claiming and stamping are one statement, so calling
    /// this in a loop walks the due rows exactly once across every replica.
    /// </summary>
    /// <param name="now">The one instant the pass runs at. The cutoff is derived from it here rather than
    /// taken as a second parameter, so no caller can hand in a pair that disagrees.</param>
    public Task<StoredSsoConfig?> ClaimDueRecheckAsync(
        DateTimeOffset now, TimeSpan recheckAfter, CancellationToken cancellationToken = default) =>
        QueryOneAsync(
            ClaimDueSql,
            cmd =>
            {
                cmd.Parameters.AddWithValue("now", now);
                cmd.Parameters.AddWithValue("due", now - recheckAfter);
            },
            cancellationToken);

    /// <summary>Starts the grace period for a claim whose record stopped resolving, returning the instant
    /// it started. Null when the claim was already failing, or is no longer verified: either way this call
    /// is not the one that owes the org a notice.</summary>
    public Task<DateTimeOffset?> RecordVerificationFailureAsync(
        long orgId, string emailDomain, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        TimestampAsync(
            RecordFailureSql,
            cmd =>
            {
                cmd.Parameters.AddWithValue("org", orgId);
                cmd.Parameters.AddWithValue("domain", emailDomain);
                cmd.Parameters.AddWithValue("now", now);
            },
            cancellationToken);

    /// <summary>Ends the grace period because the record resolved again. True when the claim had been
    /// failing, which is the only case worth logging.</summary>
    public async Task<bool> ClearVerificationFailureAsync(
        long orgId, string emailDomain, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ClearFailureSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("domain", emailDomain);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>
    /// Stops a claim routing because its record has been missing for longer than the grace period at
    /// <paramref name="now"/>, returning the instant it went missing. Null when the grace period has not
    /// run out, so the caller sends one notice per claim rather than one per pass.
    ///
    /// Takes the instant and applies <see cref="DomainVerification.VerificationGrace"/> here, rather than
    /// taking the cutoff a caller worked out. The grace period is a rule with one home, and a cutoff
    /// parameter is an invitation to compute it somewhere else and get it wrong.
    /// </summary>
    public Task<DateTimeOffset?> LapseVerificationAsync(
        long orgId, string emailDomain, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        TimestampAsync(
            LapseSql,
            cmd =>
            {
                cmd.Parameters.AddWithValue("org", orgId);
                cmd.Parameters.AddWithValue("domain", emailDomain);
                cmd.Parameters.AddWithValue("cutoff", now - DomainVerification.VerificationGrace);
            },
            cancellationToken);

    /// <summary>Runs a guarded UPDATE that returns one timestamp, and answers null when it matched no row.
    /// Read through a reader rather than ExecuteScalar: Npgsql maps timestamptz to DateTime by default, so
    /// casting the scalar to DateTimeOffset? yields null and every success reads as no row matched.</summary>
    private async Task<DateTimeOffset?> TimestampAsync(
        string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? reader.GetFieldValue<DateTimeOffset>(0)
            : null;
    }
}

/// <summary>Another org has already proved control of this domain, so this claim cannot be verified. A
/// distinct type because the caller answers it differently from every other write failure: it is the one
/// outcome where the org's own DNS was right and the answer is still no.</summary>
public sealed class DomainAlreadyVerifiedException(string emailDomain, Exception inner)
    : Exception($"another org holds a verified SSO claim on '{emailDomain}'", inner);
