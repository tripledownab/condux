using Condux.Core.Auth;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A member of an org (joined with the user's email for display).</summary>
public sealed record OrgMember(long UserId, string Email, OrgRole Role, DateTimeOffset CreatedAt);

/// <summary>An org a user belongs to, with that user's role in it.</summary>
public sealed record OrgMembership(Org Org, OrgRole Role);

/// <summary>Org membership + role lookups — the control-plane's tenancy/authorization store.</summary>
public sealed class OrgMemberRepository(string connectionString)
{
    // Upsert so accepting an invite (or re-adding) updates the role rather than failing.
    private const string AddSql = """
        INSERT INTO org_members (org_id, user_id, role)
        VALUES (@org, @user, @role)
        ON CONFLICT (org_id, user_id) DO UPDATE SET role = EXCLUDED.role;
        """;

    private const string GetRoleSql =
        "SELECT role FROM org_members WHERE org_id = @org AND user_id = @user;";

    private const string ListMembersSql = """
        SELECT m.user_id, u.email, m.role, m.created_at
        FROM org_members m JOIN users u ON u.id = m.user_id
        WHERE m.org_id = @org ORDER BY m.created_at, m.user_id;
        """;

    // The full org column list, shared with OrgRepository, then the role. A hand-picked subset here is
    // how the dashboard came to see fix_execution as hosted regardless of the row: every column the
    // select forgot silently became the record's default.
    private static readonly string ListOrgsSql = $"""
        SELECT {OrgRepository.QualifiedColumns("o")}, m.role
        FROM org_members m JOIN orgs o ON o.id = m.org_id
        WHERE m.user_id = @user ORDER BY o.id;
        """;

    private const string UpdateRoleSql =
        "UPDATE org_members SET role = @role WHERE org_id = @org AND user_id = @user;";

    private const string RemoveSql =
        "DELETE FROM org_members WHERE org_id = @org AND user_id = @user;";

    private const string CountOwnersSql =
        "SELECT count(*) FROM org_members WHERE org_id = @org AND role = @owner;";

    public async Task AddAsync(long orgId, long userId, OrgRole role, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(AddSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("role", (short)role);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The caller's role in the org, or null if they are not a member. The authz primitive.</summary>
    public async Task<OrgRole?> GetRoleAsync(long orgId, long userId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(GetRoleSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("user", userId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? null : (OrgRole)Convert.ToInt16(result);
    }

    public async Task<IReadOnlyList<OrgMember>> ListByOrgAsync(long orgId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListMembersSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);

        var list = new List<OrgMember>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new OrgMember(
                reader.GetInt64(0), reader.GetString(1), (OrgRole)reader.GetInt16(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return list;
    }

    public async Task<IReadOnlyList<OrgMembership>> ListOrgsForUserAsync(long userId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListOrgsSql, conn);
        cmd.Parameters.AddWithValue("user", userId);

        var list = new List<OrgMembership>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var roleOrdinal = -1;
        while (await reader.ReadAsync(ct))
        {
            if (roleOrdinal < 0)
            {
                roleOrdinal = reader.GetOrdinal("role");
            }
            list.Add(new OrgMembership(
                OrgRepository.Read(reader), (OrgRole)reader.GetInt16(roleOrdinal)));
        }
        return list;
    }

    public async Task<bool> UpdateRoleAsync(long orgId, long userId, OrgRole role, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(UpdateRoleSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("role", (short)role);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RemoveAsync(long orgId, long userId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RemoveSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("user", userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>How many owners the org has — used to refuse removing/demoting the last one.</summary>
    public async Task<int> CountOwnersAsync(long orgId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(CountOwnersSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("owner", (short)OrgRole.Owner);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}
