using System.Text;
using Condux.Core.Issues;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Translates a parsed <see cref="IssueFilter"/> into the shared WHERE fragment, its bound
/// parameters and an injection-safe ORDER BY column. The issue list and its facet counts both build on
/// this, so the query grammar lives in exactly one place. <see cref="Where"/> and <see cref="AddParams"/>
/// are kept in lockstep: the fragment only references params the latter binds.</summary>
internal static class IssueFilterSql
{
    /// <summary>The API sort names map to fixed columns; anything unknown falls back to last_seen.</summary>
    public static string OrderColumn(string sort) => sort switch
    {
        "firstSeen" => "first_seen",
        "events" => "event_count",
        "severity" => "level",
        _ => "last_seen",
    };

    public static string Where(IssueFilter filter)
    {
        var sb = new StringBuilder(" WHERE project_id = @project");
        if (filter.Status is not null)
        {
            sb.Append(" AND status = @status");
        }
        if (filter.Level is not null)
        {
            sb.Append(" AND level = @level");
        }
        if (filter.Assigned is true)
        {
            sb.Append(" AND assignee_user_id IS NOT NULL");
        }
        else if (filter.Assigned is false)
        {
            sb.Append(" AND assignee_user_id IS NULL");
        }
        if (filter.AssignedToMe)
        {
            sb.Append(" AND assignee_user_id = @me");
        }
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            sb.Append(" AND (title ILIKE @text OR culprit ILIKE @text)");
        }
        return sb.ToString();
    }

    public static void AddParams(NpgsqlCommand cmd, long projectId, IssueFilter filter, long currentUserId)
    {
        cmd.Parameters.AddWithValue("project", projectId);
        if (filter.Status is { } status)
        {
            cmd.Parameters.AddWithValue("status", status);
        }
        if (filter.Level is { } level)
        {
            cmd.Parameters.AddWithValue("level", level);
        }
        if (filter.AssignedToMe)
        {
            cmd.Parameters.AddWithValue("me", currentUserId);
        }
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            cmd.Parameters.AddWithValue("text", $"%{filter.Text}%");
        }
    }
}
