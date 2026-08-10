using Condux.Core.Auth;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Platform-wide totals for the admin overview.</summary>
public sealed record PlatformStats(long Orgs, long Users, long Projects, long Issues);

/// <summary>An org as the platform operator sees it: identity + plan + its owner and headline counts.</summary>
public sealed record AdminOrgRow(
    long Id, string Slug, string Name, int Tier, DateTimeOffset CreatedAt,
    string? OwnerEmail, int MemberCount, int ProjectCount);

/// <summary>A user as the platform operator sees it: identity, how many orgs they belong to, and their
/// primary org (their earliest membership) so the console can "view as" that org. Org id/name are null
/// for a user who belongs to no org.</summary>
public sealed record AdminUserRow(
    long Id, string Email, DateTimeOffset CreatedAt, int OrgCount, long? OrgId, string? OrgName);

/// <summary>
/// Read-only platform-wide queries for the super-admin console. Deliberately cross-tenant (it ignores
/// org membership), so it is only ever reached behind the platform-admin gate. Lists are bounded by
/// <see cref="MaxRows"/> — a first cut without pagination.
/// </summary>
public sealed class AdminRepository(string connectionString)
{
    /// <summary>Row cap for the admin lists until real pagination lands.</summary>
    public const int MaxRows = 500;

    private const string StatsSql = """
        SELECT
            (SELECT count(*) FROM orgs),
            (SELECT count(*) FROM users),
            (SELECT count(*) FROM projects),
            (SELECT count(*) FROM issues);
        """;

    // Owner email + member/project counts via scalar sub-selects so an org with no members or projects
    // still returns a row (a LEFT JOIN + GROUP BY would too, but this keeps each count self-contained).
    private const string ListOrgsSql = """
        SELECT o.id, o.slug, o.name, o.tier, o.created_at,
            (SELECT u.email FROM org_members m JOIN users u ON u.id = m.user_id
                WHERE m.org_id = o.id AND m.role = @owner
                ORDER BY m.created_at, m.user_id LIMIT 1) AS owner_email,
            (SELECT count(*) FROM org_members m WHERE m.org_id = o.id) AS member_count,
            (SELECT count(*) FROM projects p WHERE p.org_id = o.id) AS project_count
        FROM orgs o
        ORDER BY o.id
        LIMIT @limit;
        """;

    // A single org's header (ADR-0027 admin detail), the ListOrgsSql shape keyed by id instead of paged.
    private const string GetOrgSql = """
        SELECT o.id, o.slug, o.name, o.tier, o.created_at,
            (SELECT u.email FROM org_members m JOIN users u ON u.id = m.user_id
                WHERE m.org_id = o.id AND m.role = @owner
                ORDER BY m.created_at, m.user_id LIMIT 1) AS owner_email,
            (SELECT count(*) FROM org_members m WHERE m.org_id = o.id) AS member_count,
            (SELECT count(*) FROM projects p WHERE p.org_id = o.id) AS project_count
        FROM orgs o
        WHERE o.id = @id;
        """;

    // Each user's headline plus their PRIMARY org (earliest membership) so the console can "view as" it.
    // A LATERAL join picks the one org row deterministically; null for a user in no org.
    private const string ListUsersSql = """
        SELECT u.id, u.email, u.created_at,
            (SELECT count(*) FROM org_members m WHERE m.user_id = u.id) AS org_count,
            primary_org.id AS org_id, primary_org.name AS org_name
        FROM users u
        LEFT JOIN LATERAL (
            SELECT o.id, o.name
            FROM org_members m JOIN orgs o ON o.id = m.org_id
            WHERE m.user_id = u.id
            ORDER BY m.created_at, m.org_id
            LIMIT 1
        ) primary_org ON true
        ORDER BY u.id
        LIMIT @limit;
        """;

    public async Task<PlatformStats> GetStatsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(StatsSql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new PlatformStats(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    public async Task<IReadOnlyList<AdminOrgRow>> ListOrgsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListOrgsSql, conn);
        cmd.Parameters.AddWithValue("owner", (short)OrgRole.Owner);
        cmd.Parameters.AddWithValue("limit", MaxRows);

        var list = new List<AdminOrgRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(ReadOrgRow(reader));
        }
        return list;
    }

    /// <summary>One org's header for the admin detail page (ADR-0027), or null if it no longer exists.</summary>
    public async Task<AdminOrgRow?> GetOrgAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(GetOrgSql, conn);
        cmd.Parameters.AddWithValue("owner", (short)OrgRole.Owner);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadOrgRow(reader) : null;
    }

    private static AdminOrgRow ReadOrgRow(NpgsqlDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt16(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        (int)reader.GetInt64(6), (int)reader.GetInt64(7));

    public async Task<IReadOnlyList<AdminUserRow>> ListUsersAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListUsersSql, conn);
        cmd.Parameters.AddWithValue("limit", MaxRows);

        var list = new List<AdminUserRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new AdminUserRow(
                reader.GetInt64(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                (int)reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return list;
    }
}
