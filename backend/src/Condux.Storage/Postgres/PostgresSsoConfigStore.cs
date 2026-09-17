using Condux.Core.Auth;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>An org's stored enterprise-SSO IdP config (#72). <see cref="Protocol"/> decides which halves
/// are set: OIDC carries the endpoints + client credentials (<c>ClientSecretEncrypted</c> is the sealed
/// client secret, decrypted only during the code exchange), SAML carries the Single Sign-On URL + the
/// IdP's public signing certificate, with <c>Issuer</c> holding the IdP entity ID.
///
/// <c>VerifiedAt</c> and <c>VerificationLostAt</c> are outputs (ADR-0043): the verification path owns
/// them and <see cref="PostgresSsoConfigStore.UpsertAsync"/> ignores whatever a caller puts there, as it
/// already does for <c>UpdatedAt</c>. <c>VerificationToken</c> is an input, but only when the claimed
/// domain changes, which is a decision the upsert statement makes rather than the caller.</summary>
public sealed record StoredSsoConfig(
    long OrgId, SsoProtocol Protocol, string EmailDomain, string Issuer,
    string? AuthorizationEndpoint, string? TokenEndpoint, string? ClientId, byte[]? ClientSecretEncrypted,
    string? SamlSsoUrl, string? SamlCertificate, DateTimeOffset UpdatedAt,
    string VerificationToken, DateTimeOffset? VerifiedAt, DateTimeOffset? VerificationLostAt);

/// <summary>Postgres-backed store for the per-org SSO registry: one IdP config per org (OIDC or SAML).
/// Read by org id for the Settings management endpoints, and by email domain for login routing
/// (you@acme.com -> Acme's config), which only ever resolves a domain the org has proved it controls.
///
/// This half is the config itself. Everything about proving and losing control of the claimed domain
/// (ADR-0043) is in <c>PostgresSsoConfigStore.Verification.cs</c>, which is one lifecycle rather than a
/// slice cut to fit a line count.</summary>
public sealed partial class PostgresSsoConfigStore(string connectionString)
{
    private const string Columns =
        "org_id, protocol, email_domain, issuer, authorization_endpoint, token_endpoint, client_id, "
        + "client_secret_encrypted, saml_sso_url, saml_certificate, updated_at, "
        + "verification_token, verified_at, verification_lost_at";

    private const string GetByOrgSql = $"SELECT {Columns} FROM sso_configs WHERE org_id = @org;";

    // Only a verified claim resolves. A provisional one is saved and visible to the org that made it, and
    // routes nothing, which is what closes both squatting and pre-registration (ADR-0043). The partial
    // unique index makes at most one row verified per domain, so this stays a single-row read.
    private const string GetVerifiedByDomainSql =
        $"SELECT {Columns} FROM sso_configs WHERE email_domain = @domain AND verified_at IS NOT NULL;";

    // Changing the claimed domain restarts verification: the new domain keeps the token the caller minted,
    // and the proof of the old one does not carry over. Editing anything else leaves an existing
    // verification alone, so re-saving an IdP endpoint does not sign the org out of its own SSO.
    //
    // The rule lives in the statement rather than in the caller because every writer needs it and a
    // caller-side version is one a later writer can skip.
    private const string UpsertSql = """
        INSERT INTO sso_configs
          (org_id, protocol, email_domain, issuer, authorization_endpoint, token_endpoint, client_id,
           client_secret_encrypted, saml_sso_url, saml_certificate, updated_at, verification_token)
        VALUES (@org, @protocol, @domain, @issuer, @authz, @token, @clientId, @secret, @samlUrl, @samlCert,
                now(), @verificationToken)
        ON CONFLICT (org_id) DO UPDATE
          SET protocol = @protocol, email_domain = @domain, issuer = @issuer,
              authorization_endpoint = @authz, token_endpoint = @token, client_id = @clientId,
              client_secret_encrypted = @secret, saml_sso_url = @samlUrl, saml_certificate = @samlCert,
              updated_at = now(),
              verification_token = CASE WHEN sso_configs.email_domain = EXCLUDED.email_domain
                  THEN sso_configs.verification_token ELSE EXCLUDED.verification_token END,
              verified_at = CASE WHEN sso_configs.email_domain = EXCLUDED.email_domain
                  THEN sso_configs.verified_at ELSE NULL END,
              verification_lost_at = CASE WHEN sso_configs.email_domain = EXCLUDED.email_domain
                  THEN sso_configs.verification_lost_at ELSE NULL END
        RETURNING verification_token, verified_at, verification_lost_at;
        """;

    private const string DeleteSql = "DELETE FROM sso_configs WHERE org_id = @org;";

    public Task<StoredSsoConfig?> GetAsync(long orgId, CancellationToken cancellationToken = default) =>
        QueryOneAsync(GetByOrgSql, cmd => cmd.Parameters.AddWithValue("org", orgId), cancellationToken);

    /// <summary>The org whose <em>verified</em> claim on this domain routes a login, or null when nobody
    /// has proved one. A provisional claim is deliberately invisible here.</summary>
    public Task<StoredSsoConfig?> GetVerifiedByEmailDomainAsync(
        string emailDomain, CancellationToken cancellationToken = default) =>
        QueryOneAsync(
            GetVerifiedByDomainSql, cmd => cmd.Parameters.AddWithValue("domain", emailDomain), cancellationToken);

    /// <summary>Writes the org's config and returns it with the verification state the statement settled
    /// on, so a caller never has to work out whether its own write kept or cleared the proof.</summary>
    public async Task<StoredSsoConfig> UpsertAsync(
        StoredSsoConfig config, CancellationToken cancellationToken = default)
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
        cmd.Parameters.AddWithValue("verificationToken", config.VerificationToken);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return config with
        {
            VerificationToken = reader.GetString(0),
            VerifiedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1),
            VerificationLostAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
        };
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
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetString(11), NullableTimestamp(reader, 12), NullableTimestamp(reader, 13))
            : null;
    }

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? NullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}
