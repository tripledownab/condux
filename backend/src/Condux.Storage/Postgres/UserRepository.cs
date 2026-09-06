using Condux.Core.Auth;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// A user account. <c>PasswordHash</c> is an encoded Argon2id string, or <c>null</c> for a federated
/// account (created via an identity provider like "sign in with Google") that has no local password.
/// </summary>
public sealed record User(
    long Id, string Email, string? PasswordHash, DateTimeOffset CreatedAt, DateTimeOffset? OnboardedAt,
    // Deliberately has no default. Every column here is read by at least two repositories, and a default
    // would let one of them forget the column and silently produce a value that looks like an answer.
    bool WeeklySummaryOptOut);

/// <summary>Creates and looks up user accounts for authentication.</summary>
public sealed class UserRepository(string connectionString)
{
    private const string Columns = "id, email, password_hash, created_at, onboarded_at, weekly_summary_opt_out";

    private const string InsertSql = $"""
        INSERT INTO users (email, password_hash)
        VALUES (@email, @hash)
        RETURNING {Columns};
        """;

    // Creates a federated user and their org membership together, or reports that the address is taken.
    //
    // One statement, for two separate reasons. First, the INSERT itself is what decides whether the
    // address was new: a SELECT followed by an INSERT leaves a window in which the address appears
    // between the two, and the caller cannot tell which side of it they are on. Second, a user row
    // written without its membership row would be a federated account that belongs to no org, which
    // every later sign-in would refuse rather than repair, so the two must not be able to come apart.
    // Postgres runs a data-modifying CTE to completion whether or not the main query reads it.
    private const string InsertFederatedMemberSql = $"""
        WITH created AS (
            INSERT INTO users (email, password_hash)
            VALUES (@email, NULL)
            ON CONFLICT (email) DO NOTHING
            RETURNING {Columns}
        ), membership AS (
            INSERT INTO org_members (org_id, user_id, role)
            SELECT @org, id, @role FROM created
        )
        SELECT {Columns} FROM created;
        """;

    private const string ByEmailSql = $"SELECT {Columns} FROM users WHERE email = @email;";

    private const string ByIdSql = $"SELECT {Columns} FROM users WHERE id = @id;";

    // Idempotent: onboarding completes once (the first Finish, or invite-accept). A later call is a no-op,
    // so the original completion time is preserved.
    private const string MarkOnboardedSql =
        "UPDATE users SET onboarded_at = now() WHERE id = @id AND onboarded_at IS NULL;";

    private const string SetWeeklySummaryOptOutSql =
        "UPDATE users SET weekly_summary_opt_out = @optOut WHERE id = @id;";

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

    /// <summary>
    /// Creates a federated user already joined to <paramref name="orgId"/>, and returns them. Returns
    /// null when the email is already taken, leaving both tables untouched: the caller then decides
    /// whether that existing account may sign in, which is a question this repository cannot answer.
    /// </summary>
    public async Task<User?> TryCreateFederatedMemberAsync(
        string email, long orgId, OrgRole role, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertFederatedMemberSql, conn);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("role", (short)role);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

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

    /// <summary>Sets whether this user is excluded from their org's weekly digest.</summary>
    public async Task SetWeeklySummaryOptOutAsync(long id, bool optOut, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(SetWeeklySummaryOptOutSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("optOut", optOut);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static User Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
        r.GetBoolean(5));
}
