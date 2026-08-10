using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Condux.Storage.Postgres;

/// <summary>One entry in the platform-admin audit trail: who did what, to which org/member, when.</summary>
public sealed record AdminAuditEntry(
    long Id, long ActorId, string ActorEmail, string Action,
    long? TargetOrgId, long? TargetUserId, JsonElement Details, DateTimeOffset CreatedAt);

/// <summary>
/// Reads and writes the platform-admin audit trail (ADR-0027, table <c>admin_audit</c>). Every mutating
/// action in the super-admin console records one row; the actor is always the admin's real identity, even
/// while impersonating. Cross-tenant like <see cref="AdminRepository"/>, so it only ever runs behind the
/// platform-admin gate. Lists are bounded by <see cref="MaxRows"/> (no pagination yet).
/// </summary>
public sealed class AdminAuditRepository(string connectionString)
{
    /// <summary>Row cap for the audit list until real pagination lands.</summary>
    public const int MaxRows = 500;

    private const string InsertSql = """
        INSERT INTO admin_audit (actor_id, actor_email, action, target_org_id, target_user_id, details)
        VALUES (@actorId, @actorEmail, @action, @targetOrgId, @targetUserId, @details);
        """;

    private const string ListSql = """
        SELECT id, actor_id, actor_email, action, target_org_id, target_user_id, details, created_at
        FROM admin_audit
        WHERE (@org IS NULL OR target_org_id = @org)
        ORDER BY created_at DESC, id DESC
        LIMIT @limit;
        """;

    /// <summary>Record an admin action. <paramref name="detailsJson"/> is a JSON object string (per-action
    /// context: before/after values, Stripe ids); pass "{}" when there is nothing extra.</summary>
    public async Task WriteAsync(
        long actorId, string actorEmail, string action, long? targetOrgId, long? targetUserId,
        string detailsJson, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("actorId", actorId);
        cmd.Parameters.AddWithValue("actorEmail", actorEmail);
        cmd.Parameters.AddWithValue("action", action);
        cmd.Parameters.AddWithValue("targetOrgId", (object?)targetOrgId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("targetUserId", (object?)targetUserId ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("details", NpgsqlDbType.Jsonb) { Value = detailsJson });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Recent admin actions, newest first, optionally filtered to one org.</summary>
    public async Task<IReadOnlyList<AdminAuditEntry>> ListAsync(
        int limit = 200, long? orgId = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        // Type the parameter explicitly: when orgId is null the SQL evaluates "@org IS NULL", and an untyped
        // DBNull leaves Postgres unable to resolve the parameter type there (42P08).
        cmd.Parameters.Add(new NpgsqlParameter("org", NpgsqlDbType.Bigint) { Value = (object?)orgId ?? DBNull.Value });
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, MaxRows));

        var list = new List<AdminAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            using var details = JsonDocument.Parse(reader.GetString(6));
            list.Add(new AdminAuditEntry(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                details.RootElement.Clone(), reader.GetFieldValue<DateTimeOffset>(7)));
        }
        return list;
    }
}
