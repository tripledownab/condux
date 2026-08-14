using Condux.Core.Auth;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>An org's stored enterprise-SSO IdP config (#72). <see cref="Protocol"/> decides which halves
/// are set: OIDC carries the endpoints + client credentials (<c>ClientSecretEncrypted</c> is the sealed
/// client secret, decrypted only during the code exchange), SAML carries the Single Sign-On URL + the
/// IdP's public signing certificate, with <c>Issuer</c> holding the IdP entity ID.</summary>
public sealed record StoredSsoConfig(
    long OrgId, SsoProtocol Protocol, string EmailDomain, string Issuer,
    string? AuthorizationEndpoint, string? TokenEndpoint, string? ClientId, byte[]? ClientSecretEncrypted,
    string? SamlSsoUrl, string? SamlCertificate, DateTimeOffset UpdatedAt);

/// <summary>Postgres-backed store for the per-org SSO registry: one IdP config per org (OIDC or SAML).
/// Read by email domain for login routing (you@acme.com -> Acme's config) and by org id for the Settings
/// management endpoints.</summary>
public sealed class PostgresSsoConfigStore(string connectionString)
{
    private const string Columns =
        "org_id, protocol, email_domain, issuer, authorization_endpoint, token_endpoint, client_id, "
        + "client_secret_encrypted, saml_sso_url, saml_certificate, updated_at";

    private const string GetByOrgSql = $"SELECT {Columns} FROM sso_configs WHERE org_id = @org;";
    private const string GetByDomainSql = $"SELECT {Columns} FROM sso_configs WHERE email_domain = @domain;";

    private const string UpsertSql = """
        INSERT INTO sso_configs
          (org_id, protocol, email_domain, issuer, authorization_endpoint, token_endpoint, client_id,
           client_secret_encrypted, saml_sso_url, saml_certificate, updated_at)
        VALUES (@org, @protocol, @domain, @issuer, @authz, @token, @clientId, @secret, @samlUrl, @samlCert, now())
        ON CONFLICT (org_id) DO UPDATE
          SET protocol = @protocol, email_domain = @domain, issuer = @issuer,
              authorization_endpoint = @authz, token_endpoint = @token, client_id = @clientId,
              client_secret_encrypted = @secret, saml_sso_url = @samlUrl, saml_certificate = @samlCert,
              updated_at = now();
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
        cmd.Parameters.AddWithValue("protocol", (short)config.Protocol);
        cmd.Parameters.AddWithValue("domain", config.EmailDomain);
        cmd.Parameters.AddWithValue("issuer", config.Issuer);
        cmd.Parameters.AddWithValue("authz", (object?)config.AuthorizationEndpoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("token", (object?)config.TokenEndpoint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("clientId", (object?)config.ClientId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("secret", (object?)config.ClientSecretEncrypted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("samlUrl", (object?)config.SamlSsoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("samlCert", (object?)config.SamlCertificate ?? DBNull.Value);
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
                reader.GetInt64(0), (SsoProtocol)reader.GetInt16(1), reader.GetString(2), reader.GetString(3),
                NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6),
                reader.IsDBNull(7) ? null : (byte[])reader[7],
                NullableString(reader, 8), NullableString(reader, 9),
                reader.GetFieldValue<DateTimeOffset>(10))
            : null;
    }

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
