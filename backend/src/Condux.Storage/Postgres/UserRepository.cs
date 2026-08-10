using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// A user account. <c>PasswordHash</c> is an encoded Argon2id string, or <c>null</c> for a federated
/// account (created via an identity provider like "sign in with Google") that has no local password.
/// </summary>
public sealed record User(
    long Id, string Email, string? PasswordHash, DateTimeOffset CreatedAt, DateTimeOffset? OnboardedAt);

/// <summary>Creates and looks up user accounts for authentication.</summary>
public sealed class UserRepository(string connectionString)
{
    private const string Columns = "id, email, password_hash, created_at, onboarded_at";

    private const string InsertSql = $"""
        INSERT INTO users (email, password_hash)
        VALUES (@email, @hash)
        RETURNING {Columns};
        """;

    private const string ByEmailSql = $"SELECT {Columns} FROM users WHERE email = @email;";

    private const string ByIdSql = $"SELECT {Columns} FROM users WHERE id = @id;";

    // Idempotent: onboarding completes once (the first Finish, or invite-accept). A later call is a no-op,
    // so the original completion time is preserved.
    private const string MarkOnboardedSql =
        "UPDATE users SET onboarded_at = now() WHERE id = @id AND onboarded_at IS NULL;";

    /// <summary>Creates a user. The caller passes a normalized email and an encoded password hash.</summary>
    public async Task<User> CreateAsync(string email, string passwordHash, CancellationToken ct = default) =>
        await InsertAsync(email, passwordHash, ct);

    /// <summary>
    /// Creates a federated user (no local password) for an identity-provider sign-in. The account can
    /// only authenticate through its provider until a password is set; the password login path rejects a
    /// null hash.
    /// </summary>
    public async Task<User> CreateFederatedAsync(string email, CancellationToken ct = default) =>
        await InsertAsync(email, null, ct);

    private async Task<User> InsertAsync(string email, string? passwordHash, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("hash", (object?)passwordHash ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ByEmailSql, conn);
        cmd.Parameters.AddWithValue("email", email);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<User?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ByIdSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Marks the user as having finished onboarding (idempotent — keeps the first completion time).</summary>
    public async Task MarkOnboardedAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(MarkOnboardedSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static User Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4));
}
