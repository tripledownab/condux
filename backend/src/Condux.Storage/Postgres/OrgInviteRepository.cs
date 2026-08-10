using Condux.Core.Auth;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A pending or resolved invitation for an email to join an org with a role.</summary>
public sealed record OrgInvite(
    long Id, long OrgId, string Email, OrgRole Role,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt);

/// <summary>Creates, lists, resolves, and revokes org invites. Tokens are matched by their hash.</summary>
public sealed class OrgInviteRepository(string connectionString)
{
    private const string Columns =
        "id, org_id, email, role, created_at, expires_at, accepted_at, revoked_at";

    private const string InsertSql = $"""
        INSERT INTO org_invites (org_id, email, role, token_hash, invited_by, expires_at)
        VALUES (@org, @email, @role, @hash, @by, @expires)
        RETURNING {Columns};
        """;

    // Only a live invite (not accepted, not revoked, not expired) is redeemable.
    private const string ByTokenSql = $"""
        SELECT {Columns} FROM org_invites
        WHERE token_hash = @hash AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > now();
        """;

    private const string ListPendingSql = $"""
        SELECT {Columns} FROM org_invites
        WHERE org_id = @org AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > now()
        ORDER BY created_at, id;
        """;

    private const string RevokeSql = """
        UPDATE org_invites SET revoked_at = now()
        WHERE id = @id AND org_id = @org AND accepted_at IS NULL AND revoked_at IS NULL;
        """;

    private const string MarkAcceptedSql =
        "UPDATE org_invites SET accepted_at = now() WHERE id = @id AND accepted_at IS NULL;";

    public async Task<OrgInvite> CreateAsync(
        long orgId, string email, OrgRole role, string tokenHash, long invitedBy,
        DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("role", (short)role);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        cmd.Parameters.AddWithValue("by", invitedBy);
        cmd.Parameters.AddWithValue("expires", expiresAt);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task<OrgInvite?> GetActiveByTokenHashAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ByTokenSql, conn);
        cmd.Parameters.AddWithValue("hash", tokenHash);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<OrgInvite>> ListPendingByOrgAsync(long orgId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListPendingSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);

        var list = new List<OrgInvite>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task<bool> RevokeAsync(long orgId, long inviteId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RevokeSql, conn);
        cmd.Parameters.AddWithValue("id", inviteId);
        cmd.Parameters.AddWithValue("org", orgId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> MarkAcceptedAsync(long inviteId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(MarkAcceptedSql, conn);
        cmd.Parameters.AddWithValue("id", inviteId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static OrgInvite Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), (OrgRole)r.GetInt16(3),
        r.GetFieldValue<DateTimeOffset>(4), r.GetFieldValue<DateTimeOffset>(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7));
}
