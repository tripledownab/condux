using Condux.Core.Tickets;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>An issue's ticket in a tracker workspace, at whatever point of its life it has reached.</summary>
public sealed record IssueTicket(
    Guid Id, long IssueId, TicketProvider Provider, string WorkspaceId, TicketState State,
    TicketFailure? Failure, string? Key, string? Url, bool Automatic);

/// <summary>A row the worker has claimed, with what it needs to build and send the ticket.
/// <paramref name="Age"/> is how long ago it was reserved, read on the database's clock.</summary>
public sealed record ClaimedTicket(
    Guid Id, long IssueId, long ProjectId, long OrgId, TicketProvider Provider, string WorkspaceId,
    bool Automatic, TimeSpan Age);

/// <summary>
/// The <c>issue_tickets</c> rows (ADR-0053 decision 5). Reserving is an insert that loses on the unique key,
/// so two clicks, a click racing an alert or a retry all end on one row. One worker claims pending rows and
/// is the only caller of the tracker. Every transition is a guarded UPDATE on the state it leaves, so a row
/// cannot be completed twice or moved out of a state someone else already moved it out of, and the row
/// count is the answer.
///
/// Every instant here is the database's <c>now()</c>, never one a caller passes: a claim's stamp, the
/// sweep's cutoff and a deferral's not before time all have to be read on one clock. A worker on another replica
/// sweeps this worker's claims, and an instant computed in one process before a slow pass would date a
/// claim minutes before it was made and have it swept while still in flight.
/// </summary>
public sealed partial class IssueTicketRepository(string connectionString)
{
    private const string Columns =
        "id, issue_id, provider, workspace_id, state, failure, ticket_key, ticket_url, automatic";

    /// <summary>The issue's ticket in that workspace, or null when there is none.</summary>
    public async Task<IssueTicket?> GetAsync(
        long issueId, TicketProvider provider, string workspaceId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            SELECT {Columns} FROM issue_tickets
            WHERE issue_id = @issue AND provider = @provider AND workspace_id = @workspace;
            """, conn);
        cmd.Parameters.AddWithValue("issue", issueId);
        cmd.Parameters.AddWithValue("provider", (short)provider);
        cmd.Parameters.AddWithValue("workspace", workspaceId);
        return await ReadOneAsync(cmd, ct);
    }

    /// <summary>
    /// Reserve a ticket for the issue, or return the one already reserved. The id is a fresh UUID v4 because
    /// it is also sent to Linear as the issue's own id, and Linear requires that format.
    /// </summary>
    public async Task<(IssueTicket Ticket, bool Reserved)> ReserveAsync(
        long issueId, TicketProvider provider, string workspaceId, long? createdBy, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO issue_tickets (id, issue_id, provider, workspace_id, state, created_by)
            VALUES (@id, @issue, @provider, @workspace, {(short)TicketState.Pending}, @by)
            ON CONFLICT (issue_id, provider, workspace_id) DO NOTHING
            RETURNING {Columns};
            """, conn))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("issue", issueId);
            insert.Parameters.AddWithValue("provider", (short)provider);
            insert.Parameters.AddWithValue("workspace", workspaceId);
            insert.Parameters.AddWithValue("by", (object?)createdBy ?? DBNull.Value);
            if (await ReadOneAsync(insert, ct) is { } reserved)
            {
                return (reserved, true);
            }
        }
        // Lost the insert, so a row exists. Only an unlink or the issue's own deletion removes one, so a
        // miss here is one of those racing this request, and it fails the request rather than guessing.
        return (await GetAsync(issueId, provider, workspaceId, ct)
            ?? throw new InvalidOperationException("A reserved ticket vanished between insert and read."), false);
    }

    /// <summary>
    /// Claim the oldest pending row. An expired claim is never treated as free, unlike the runner lease
    /// store, because the call it covered may already have reached the tracker; see
    /// <see cref="MarkStaleUncertainAsync"/>.
    /// </summary>
    /// <remarks>
    /// An org with a row the tracker held off has none of its rows claimed until that one is due: Linear's rate limits
    /// and grants are per org, so its next row would meet the same answer, and deferring rows one by one
    /// would spend a call per pass and leave a backlog that all falls due at once, ahead of other orgs.
    ///
    /// Two guards keep a second worker off a row the first is claiming, and either alone would do. The
    /// subquery's <c>FOR UPDATE SKIP LOCKED</c> takes the row's lock and passes over one another worker
    /// holds. The outer <c>t.state</c> is what a writer that waited on that lock rechecks once it gets it,
    /// which is all that would stand if the subquery ever lost its locking clause, since the outer UPDATE
    /// otherwise rechecks only the id. A test forces the overlap and fails with both removed.
    /// </remarks>
    public async Task<ClaimedTicket?> ClaimNextAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE issue_tickets t SET state = {(short)TicketState.Claimed}, claimed_at = now(), updated_at = now()
            FROM issues i JOIN projects p ON p.id = i.project_id
            WHERE t.id = (SELECT c.id FROM issue_tickets c
                          JOIN issues ci ON ci.id = c.issue_id JOIN projects cp ON cp.id = ci.project_id
                          WHERE c.state = {(short)TicketState.Pending}
                            AND (c.not_before IS NULL OR c.not_before <= now())
                            AND NOT EXISTS (
                                SELECT 1 FROM issue_tickets d
                                JOIN issues di ON di.id = d.issue_id JOIN projects dp ON dp.id = di.project_id
                                WHERE dp.org_id = cp.org_id AND d.state = {(short)TicketState.Pending}
                                  AND d.held_by_tracker AND d.not_before > now())
                          ORDER BY c.created_at LIMIT 1 FOR UPDATE OF c SKIP LOCKED)
              AND t.state = {(short)TicketState.Pending}
              AND i.id = t.issue_id
            RETURNING t.id, t.issue_id, i.project_id, p.org_id, t.provider, t.workspace_id, t.automatic,
                now() - t.created_at;
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ClaimedTicket(
                reader.GetGuid(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                (TicketProvider)reader.GetInt16(4), reader.GetString(5), reader.GetBoolean(6),
                reader.GetFieldValue<TimeSpan>(7))
            : null;
    }

    /// <summary>Record what the claimed attempt established. A retry later goes back to pending, not to be
    /// claimed again for <paramref name="retryDelay"/>. False when the row was not still claimed, which means
    /// it was already marked uncertain for taking too long and the answer arrived after a person was told to
    /// check.</summary>
    public async Task<bool> RecordAsync(Guid id, TicketOutcome outcome, TimeSpan retryDelay, CancellationToken ct = default)
    {
        var (state, failure) = outcome.Kind switch
        {
            TicketOutcomeKind.Created => (TicketState.Created, (TicketFailure?)null),
            TicketOutcomeKind.Failed => (TicketState.Failed, outcome.Failure),
            TicketOutcomeKind.RetryLater => (TicketState.Pending, null),
            _ => (TicketState.Uncertain, null),
        };
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE issue_tickets SET state = @state, failure = @failure, ticket_key = @key, ticket_url = @url,
                claimed_at = NULL, updated_at = now(),
                not_before = CASE WHEN @state = {(short)TicketState.Pending} THEN now() + @delay ELSE NULL END,
                held_by_tracker = @state = {(short)TicketState.Pending}
            WHERE id = @id AND state = {(short)TicketState.Claimed};
            """, conn);
        cmd.Parameters.AddWithValue("delay", retryDelay);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("state", (short)state);
        cmd.Parameters.AddWithValue("failure", failure is { } f ? (short)f : DBNull.Value);
        cmd.Parameters.AddWithValue("key", (object?)outcome.Created?.Key ?? DBNull.Value);
        cmd.Parameters.AddWithValue("url", (object?)outcome.Created?.Url ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Put a claimed row back unsent, not to be claimed for <paramref name="delay"/>, for a reason
    /// of the row's own rather than the tracker's, so it pauses nothing else in the org. False when it was
    /// not still claimed.</summary>
    public async Task<bool> PutBackAsync(Guid id, TimeSpan delay, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE issue_tickets SET state = {(short)TicketState.Pending}, claimed_at = NULL,
                not_before = now() + @delay, held_by_tracker = false, updated_at = now()
            WHERE id = @id AND state = {(short)TicketState.Claimed};
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("delay", delay);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Claims held longer than <paramref name="timeout"/> become uncertain: the worker that held them
    /// stopped without recording an answer, and the call may have reached the tracker. Returns the project of
    /// each row moved, so each project's rail can be told.</summary>
    public async Task<IReadOnlyList<long>> MarkStaleUncertainAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE issue_tickets t SET state = {(short)TicketState.Uncertain}, claimed_at = NULL, updated_at = now()
            FROM issues i
            WHERE t.state = {(short)TicketState.Claimed} AND t.claimed_at < now() - @timeout AND i.id = t.issue_id
            RETURNING i.project_id;
            """, conn);
        cmd.Parameters.AddWithValue("timeout", timeout);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var projects = new List<long>();
        while (await reader.ReadAsync(ct))
        {
            projects.Add(reader.GetInt64(0));
        }
        return projects;
    }

    /// <summary>Send a failed or uncertain row again, under the same id so the tracker is asked for the
    /// same issue. False when the row is in neither state.</summary>
    public async Task<bool> RetryAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE issue_tickets SET state = {(short)TicketState.Pending}, failure = NULL, not_before = NULL,
                held_by_tracker = false,
                updated_at = now()
            WHERE id = @id AND state IN ({(short)TicketState.Failed}, {(short)TicketState.Uncertain});
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Forget the link, so a new ticket can be created: the answer to a ticket deleted in the
    /// tracker. A row the worker holds is not removed, since its answer would then have nowhere to land.</summary>
    public async Task<bool> UnlinkAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"DELETE FROM issue_tickets WHERE id = @id AND state <> {(short)TicketState.Claimed};", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task<IssueTicket?> ReadOneAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new IssueTicket(
                r.GetGuid(0), r.GetInt64(1), (TicketProvider)r.GetInt16(2), r.GetString(3), (TicketState)r.GetInt16(4),
                r.IsDBNull(5) ? null : (TicketFailure)r.GetInt16(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetString(7), r.GetBoolean(8))
            : null;
    }
}
