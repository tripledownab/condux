using Condux.Core.Tickets;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>What an automatic reservation came to.</summary>
public enum AutoReservation
{
    Reserved = 1,

    /// <summary>The issue already had a ticket in this workspace, so nothing was reserved or counted.</summary>
    AlreadyTicketed = 2,

    /// <summary>The project has opened its ceiling of automatic tickets today, so nothing was reserved.</summary>
    CeilingReached = 3,
}

// The reservation the auto-create setting makes (ADR-0053 slice 2), in its own file because it is the one
// write here that spends a budget.
public sealed partial class IssueTicketRepository
{
    /// <summary>
    /// Reserve a ticket nobody asked for, counted against the project's daily ceiling. The count is taken in
    /// the same transaction as the insert and only when the insert wins, so an issue that already has a
    /// ticket spends nothing, and a reservation the ceiling refuses is rolled back. The counter is a guarded
    /// upsert because a count of existing rows is passed by two reservations made at once.
    /// </summary>
    public async Task<AutoReservation> ReserveAutomaticAsync(
        long issueId, long projectId, TicketProvider provider, string workspaceId, int perDay, TimeSpan settleDelay,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO issue_tickets (id, issue_id, provider, workspace_id, state, automatic, not_before)
            VALUES (@id, @issue, @provider, @workspace, {(short)TicketState.Pending}, true, now() + @settle)
            ON CONFLICT (issue_id, provider, workspace_id) DO NOTHING;
            """, conn, tx))
        {
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("issue", issueId);
            insert.Parameters.AddWithValue("provider", (short)provider);
            insert.Parameters.AddWithValue("workspace", workspaceId);
            insert.Parameters.AddWithValue("settle", settleDelay);
            if (await insert.ExecuteNonQueryAsync(ct) == 0)
            {
                await tx.CommitAsync(ct);
                return AutoReservation.AlreadyTicketed;
            }
        }
        await using (var count = new NpgsqlCommand("""
            INSERT INTO tracker_auto_tickets_daily (project_id, day, created)
            VALUES (@project, (now() AT TIME ZONE 'UTC')::date, 1)
            ON CONFLICT (project_id, day) DO UPDATE SET created = tracker_auto_tickets_daily.created + 1
            WHERE tracker_auto_tickets_daily.created < @ceiling;
            """, conn, tx))
        {
            count.Parameters.AddWithValue("project", projectId);
            count.Parameters.AddWithValue("ceiling", perDay);
            if (await count.ExecuteNonQueryAsync(ct) == 0)
            {
                await tx.RollbackAsync(ct);
                return AutoReservation.CeilingReached;
            }
        }
        await tx.CommitAsync(ct);
        return AutoReservation.Reserved;
    }

    /// <summary>How many tickets the project has opened on its own today (UTC), for its Linear tab.</summary>
    public async Task<int> CountAutomaticTodayAsync(long projectId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT created FROM tracker_auto_tickets_daily WHERE project_id = @project AND day = (now() AT TIME ZONE 'UTC')::date;", conn);
        cmd.Parameters.AddWithValue("project", projectId);
        return await cmd.ExecuteScalarAsync(ct) is int created ? created : 0;
    }
}
