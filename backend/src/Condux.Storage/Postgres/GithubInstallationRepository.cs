using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A GitHub App installation tied to a Condux org.</summary>
public sealed record GithubInstallation(
    long InstallationId, long OrgId, string? AccountLogin, DateTimeOffset CreatedAt);

/// <summary>Maps GitHub App installations to orgs (#61). The Setup URL links one; the webhook removes it.</summary>
public sealed class GithubInstallationRepository(string connectionString)
{
    // Re-installing (or re-linking) refreshes the row rather than failing on the unique id, but only for
    // the org that already holds it: the WHERE makes the statement itself refuse to move an installation
    // between orgs, so the row count is the answer and no caller can read-then-write around it. Freeing
    // one for another org to claim is a delete, which is what disconnect does. A known account login is
    // kept when the caller doesn't have one, so a relink can't blank what a webhook set.
    private const string LinkSql = """
        INSERT INTO github_installations (installation_id, org_id, account_login)
        VALUES (@installation, @org, @login)
        ON CONFLICT (installation_id) DO UPDATE
        SET account_login = COALESCE(EXCLUDED.account_login, github_installations.account_login)
        WHERE github_installations.org_id = EXCLUDED.org_id;
        """;

    private const string ByIdSql = """
        SELECT installation_id, org_id, account_login, created_at
        FROM github_installations WHERE installation_id = @installation;
        """;

    private const string SetLoginSql =
        "UPDATE github_installations SET account_login = @login WHERE installation_id = @installation;";

    private const string DeleteSql =
        "DELETE FROM github_installations WHERE installation_id = @installation;";

    private const string DeleteByOrgSql =
        "DELETE FROM github_installations WHERE org_id = @org;";

    private const string ByOrgSql = """
        SELECT installation_id, org_id, account_login, created_at
        FROM github_installations WHERE org_id = @org ORDER BY created_at;
        """;

    /// <summary>
    /// Tie an installation to an org, reporting whether it is now this org's. False means another org
    /// already holds it and nothing was written: reaching an installation on GitHub takes only read
    /// access, so a caller who can name an id must not be able to take it from the tenant using it.
    /// </summary>
    public async Task<bool> LinkAsync(
        long installationId, long orgId, string? accountLogin = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(LinkSql, conn);
        cmd.Parameters.AddWithValue("installation", installationId);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("login", (object?)accountLogin ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>One installation by its GitHub id, or null if we hold no link for it.</summary>
    public async Task<GithubInstallation?> GetAsync(long installationId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ByIdSql, conn);
        cmd.Parameters.AddWithValue("installation", installationId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new GithubInstallation(
                reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    /// <summary>Best-effort: record the GitHub account the app is installed on (from a webhook payload).</summary>
    public async Task SetAccountLoginAsync(long installationId, string login, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(SetLoginSql, conn);
        cmd.Parameters.AddWithValue("installation", installationId);
        cmd.Parameters.AddWithValue("login", login);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Remove an installation (the app was uninstalled — a webhook <c>installation.deleted</c>).</summary>
    public async Task DeleteAsync(long installationId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(DeleteSql, conn);
        cmd.Parameters.AddWithValue("installation", installationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Drop an org's link to its installation, returning how many rows went. The app stays installed on
    /// GitHub: this only stops Condux using it, which is what makes the action reversible (connecting
    /// again finds the same installation) and what lets another org claim it.
    /// </summary>
    public async Task<int> DeleteByOrgAsync(long orgId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(DeleteByOrgSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<GithubInstallation>> GetByOrgAsync(long orgId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ByOrgSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);

        var list = new List<GithubInstallation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new GithubInstallation(
                reader.GetInt64(0), reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return list;
    }
}
