using Condux.Core.Tickets;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A label a project puts on its tickets, kept with the name it had when chosen.</summary>
public sealed record TrackerLabel(string Id, string Name);

/// <summary>Where a project's tickets land in a tracker and how: for Linear, a team and its labels, each kept
/// with the name it had when chosen, and whether new errors open a ticket on their own.
/// <paramref name="NeedsAttentionAt"/> is set when the tracker refused that team.</summary>
public sealed record ProjectTrackerMapping(
    long ProjectId, TicketProvider Provider, string TargetId, string TargetName, DateTimeOffset? NeedsAttentionAt,
    bool AutoCreate, IReadOnlyList<TrackerLabel> Labels);

/// <summary>A project's choice of tracker team (<c>project_tracker_mappings</c>, ADR-0053).</summary>
public sealed class ProjectTrackerMappingRepository(string connectionString)
{
    public async Task<ProjectTrackerMapping?> GetAsync(long projectId, TicketProvider provider, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT project_id, provider, target_id, target_name, needs_attention_at, auto_create, label_ids, label_names
            FROM project_tracker_mappings WHERE project_id = @project AND provider = @provider;
            """, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("provider", (short)provider);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new ProjectTrackerMapping(
                reader.GetInt64(0), (TicketProvider)reader.GetInt16(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.GetBoolean(5),
                [.. reader.GetFieldValue<string[]>(6).Zip(reader.GetFieldValue<string[]>(7), (id, name) => new TrackerLabel(id, name))])
            : null;
    }

    /// <summary>Set the project's whole choice: team, labels and whether new errors open a ticket. A whole
    /// replacement rather than a patch, so a label from a team just replaced cannot linger. It clears any
    /// attention mark, since the mark was about the choice being replaced.</summary>
    public async Task SetAsync(
        long projectId, TicketProvider provider, string targetId, string targetName,
        IReadOnlyList<TrackerLabel> labels, bool autoCreate, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO project_tracker_mappings (project_id, provider, target_id, target_name, label_ids, label_names, auto_create)
            VALUES (@project, @provider, @target, @name, @label_ids, @label_names, @auto)
            ON CONFLICT (project_id, provider) DO UPDATE SET
                target_id = EXCLUDED.target_id, target_name = EXCLUDED.target_name,
                label_ids = EXCLUDED.label_ids, label_names = EXCLUDED.label_names, auto_create = EXCLUDED.auto_create,
                needs_attention_at = NULL, updated_at = now();
            """, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("provider", (short)provider);
        cmd.Parameters.AddWithValue("target", targetId);
        cmd.Parameters.AddWithValue("name", targetName);
        cmd.Parameters.AddWithValue("label_ids", labels.Select(l => l.Id).ToArray());
        cmd.Parameters.AddWithValue("label_names", labels.Select(l => l.Name).ToArray());
        cmd.Parameters.AddWithValue("auto", autoCreate);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Mark the mapping as needing attention, only while it still holds the team and labels the
    /// refused ticket was sent with, so a refusal that lands after an admin changed either does not flag the
    /// choice that replaced them.</summary>
    public async Task MarkNeedsAttentionAsync(
        long projectId, TicketProvider provider, string refusedTargetId, IReadOnlyList<string> refusedLabelIds,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE project_tracker_mappings SET needs_attention_at = now(), updated_at = now()
            WHERE project_id = @project AND provider = @provider AND target_id = @target
              AND label_ids = @labels AND needs_attention_at IS NULL;
            """, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("provider", (short)provider);
        cmd.Parameters.AddWithValue("target", refusedTargetId);
        cmd.Parameters.AddWithValue("labels", refusedLabelIds.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(long projectId, TicketProvider provider, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM project_tracker_mappings WHERE project_id = @project AND provider = @provider;", conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("provider", (short)provider);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Remove every mapping in the org, which is what reconnecting to a different workspace
    /// requires: a team id from one workspace means nothing in another.</summary>
    public async Task DeleteForOrgAsync(long orgId, TicketProvider provider, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            DELETE FROM project_tracker_mappings m USING projects p
            WHERE m.project_id = p.id AND p.org_id = @org AND m.provider = @provider;
            """, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("provider", (short)provider);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The names of the org's projects that have a mapping, for the disconnect dialog.</summary>
    public async Task<IReadOnlyList<string>> ListMappedProjectNamesAsync(
        long orgId, TicketProvider provider, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT p.name FROM project_tracker_mappings m JOIN projects p ON p.id = m.project_id
            WHERE p.org_id = @org AND m.provider = @provider ORDER BY p.name;
            """, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("provider", (short)provider);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var names = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }
        return names;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
