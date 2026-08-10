using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>An org's stored LLM provider config (#65). <c>KeyEncrypted</c> is the sealed API key
/// (never the plaintext); only the Conductor decrypts it at fix time.</summary>
public sealed record StoredLlmConfig(
    long OrgId, string Provider, string Model, string BaseUrl, byte[] KeyEncrypted, DateTimeOffset UpdatedAt);

/// <summary>Read side of the LLM config, so the Conductor can resolve an org's BYO key without
/// depending on the full store (and unit-test the resolution with a fake).</summary>
public interface ILlmConfigReader
{
    Task<StoredLlmConfig?> GetAsync(long orgId, CancellationToken cancellationToken = default);
}

/// <summary>Postgres-backed store for the BYO-key registry: one LLM config per org, the API key held
/// encrypted at rest (<c>llm_configs.key_encrypted</c>).</summary>
public sealed class PostgresLlmConfigStore(string connectionString) : ILlmConfigReader
{
    private const string GetSql = """
        SELECT org_id, provider, model, base_url, key_encrypted, updated_at
        FROM llm_configs WHERE org_id = @org;
        """;

    private const string UpsertSql = """
        INSERT INTO llm_configs (org_id, provider, model, base_url, key_encrypted, updated_at)
        VALUES (@org, @provider, @model, @base, @key, now())
        ON CONFLICT (org_id) DO UPDATE
          SET provider = @provider, model = @model, base_url = @base,
              key_encrypted = @key, updated_at = now();
        """;

    private const string DeleteSql = "DELETE FROM llm_configs WHERE org_id = @org;";

    public async Task<StoredLlmConfig?> GetAsync(long orgId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(GetSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredLlmConfig(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                (byte[])reader[4], reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    public async Task UpsertAsync(StoredLlmConfig config, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UpsertSql, conn);
        cmd.Parameters.AddWithValue("org", config.OrgId);
        cmd.Parameters.AddWithValue("provider", config.Provider);
        cmd.Parameters.AddWithValue("model", config.Model);
        cmd.Parameters.AddWithValue("base", config.BaseUrl);
        cmd.Parameters.AddWithValue("key", config.KeyEncrypted);
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
}
