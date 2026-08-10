using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>An org's stored enterprise-SSO OIDC config (#72). <c>ClientSecretEncrypted</c> is the sealed
/// client secret (never the plaintext); only the SSO login path decrypts it during the code exchange.</summary>
public sealed record StoredSsoConfig(
    long OrgId, string EmailDomain, string Issuer, string AuthorizationEndpoint, string TokenEndpoint,
    string ClientId, byte[] ClientSecretEncrypted, DateTimeOffset UpdatedAt);

/// <summary>Postgres-backed store for the per-org SSO registry: one OIDC IdP config per org, the client
/// secret held encrypted at rest (<c>sso_configs.client_secret_encrypted</c>). Read by email domain for
/// login routing (you@acme.com -> Acme's config) and by org id for the Settings management endpoints.</summary>
public sealed class PostgresSsoConfigStore(string connectionString)
{
    private const string Columns =
        "org_id, email_domain, issuer, authorization_endpoint, token_endpoint, client_id, client_secret_encrypted, updated_at";

    private const string GetByOrgSql = $"SELECT {Columns} FROM sso_configs WHERE org_id = @org;";
    private const string GetByDomainSql = $"SELECT {Columns} FROM sso_configs WHERE email_domain = @domain;";

    private const string UpsertSql = """
        INSERT INTO sso_configs
          (org_id, email_domain, issuer, authorization_endpoint, token_endpoint, client_id, client_secret_encrypted, updated_at)
        VALUES (@org, @domain, @issuer, @authz, @token, @clientId, @secret, now())
        ON CONFLICT (org_id) DO UPDATE
          SET email_domain = @domain, issuer = @issuer, authorization_endpoint = @authz,
              token_endpoint = @token, client_id = @clientId, client_secret_encrypted = @secret, updated_at = now();
        """;

    private const string DeleteSql = "DELETE FROM sso_configs WHERE org_id = @org;";

    public Task<StoredSsoConfig?> GetAsync(long orgId, CancellationToken cancellationToken = default) =>
        QueryOneAsync(GetByOrgSql, cmd => cmd.Parameters.AddWithValue("org", orgId), cancellationToken);

    public Task<StoredSsoConfig?> GetByEmailDomainAsync(string emailDomain, CancellationToken cancellationToken = default) =>
        QueryOneAsync(GetByDomainSql, cmd => cmd.Parameters.AddWithValue("domain", emailDomain), cancellationToken);

    public async Task UpsertAsync(StoredSsoConfig config, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UpsertSql, conn);
        cmd.Parameters.AddWithValue("org", config.OrgId);
        cmd.Parameters.AddWithValue("domain", config.EmailDomain);
        cmd.Parameters.AddWithValue("issuer", config.Issuer);
        cmd.Parameters.AddWithValue("authz", config.AuthorizationEndpoint);
        cmd.Parameters.AddWithValue("token", config.TokenEndpoint);
        cmd.Parameters.AddWithValue("clientId", config.ClientId);
        cmd.Parameters.AddWithValue("secret", config.ClientSecretEncrypted);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Removes the org's config. False when there was none.</summary>
    public async Task<bool> DeleteAsync(long orgId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(DeleteSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private async Task<StoredSsoConfig?> QueryOneAsync(
        string sql, Action<NpgsqlCommand> bind, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        bind(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredSsoConfig(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), (byte[])reader[6], reader.GetFieldValue<DateTimeOffset>(7))
            : null;
    }
}
