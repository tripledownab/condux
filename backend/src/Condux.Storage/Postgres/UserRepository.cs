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
    bool WeeklySummaryOptOut,
    // When a password cooldown ends, so the sign-in path learns it from the lookup it already does.
    // The failure COUNT is deliberately absent: nothing outside the counter needs the number, and
    // handing it out invites a read-then-write around the guarded UPDATE, which is the exact race that
    // statement exists to prevent.
    DateTimeOffset? LoginLockedUntil);

/// <summary>Creates and looks up user accounts for authentication.</summary>
public sealed partial class UserRepository(string connectionString)
{
    private const string Columns =
        "id, email, password_hash, created_at, onboarded_at, weekly_summary_opt_out, login_locked_until";

    // DO NOTHING rather than letting the unique index throw, for the reason spelled out on the
    // federated insert below: every caller looks the address up first, and between that read and this
    // write the address can appear. Without this, two sign-ups racing the same address gave one caller
    // a 500 carrying a Postgres exception, which the platform then filed against itself as a defect.
    // Reproducible, not rare: a double-clicked submit button is enough. The INSERT is therefore what
    // decides whether the address was new, and no row back means somebody else won.
    //
    // NAMING (email) IS LOAD-BEARING, and dropping it would read as a simplification. Postgres: "For
    // ON CONFLICT DO NOTHING, it is optional to specify a conflict_target; when omitted, conflicts
    // with all usable constraints (and unique indexes) are handled." Untargeted, this would swallow
    // every future constraint violation on the table and report it to the caller as a taken address.
    private const string InsertSql = $"""
        INSERT INTO users (email, password_hash)
        VALUES (@email, @hash)
        ON CONFLICT (email) DO NOTHING
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

    /// <summary>
    /// Creates a user, or returns null when the address was taken. The caller passes a normalized
    /// email and an encoded password hash. Named Try because the null is the point: the address can
    /// appear between the caller's own lookup and this insert, and the insert is what settles it.
    /// </summary>
    public async Task<User?> TryCreateAsync(string email, string passwordHash, CancellationToken ct = default) =>
        await InsertAsync(email, passwordHash, ct);

    /// <summary>
    /// Creates a federated user (no local password) for an identity-provider sign-in. The account can
    /// only authenticate through its provider until a password is set; the password login path rejects a
    /// null hash.
    /// </summary>
    public async Task<User?> TryCreateFederatedAsync(string email, CancellationToken ct = default) =>
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

    private async Task<User?> InsertAsync(string email, string? passwordHash, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("email", email);
        cmd.Parameters.AddWithValue("hash", (object?)passwordHash ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
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
    // Setting a password and spending that user's outstanding reset links are one act, not two, so they
    // are one statement. Split apart, a caller can do the first and forget the second, and one of the two
    // callers did: someone who changed their password from Settings because they suspected trouble left
    // any reset link an attacker had already triggered live for the rest of its hour, able to overwrite
    // the password they had just chosen. Written as a CTE so neither half can happen without the other.
    // Clearing the guessing counter belongs to the same act for the same reason. The emailed reset is the
    // way out of an attack, and an attacker only has to fail five sign-ins to start a cooldown; left
    // standing, it survives the reset, so the user sets a new password and is then told it is wrong for
    // the rest of the window. Proving control of the mailbox is a stronger claim than the password the
    // counter was protecting, so there is nothing to weaken by clearing it here.
    private static readonly string SetPasswordSql = $"""
        WITH spent AS (
            UPDATE password_resets SET used_at = now()
            WHERE user_id = @id AND used_at IS NULL
        )
        UPDATE users SET password_hash = @hash, {FailureCooldown.ClearAssignments(LoginFailures)}
        WHERE id = @id;
        """;

    /// <summary>
    /// Replaces a user's password hash and spends every outstanding reset link they have. Used by both a
    /// signed-in change and an emailed reset, so it says nothing about which: the caller has already
    /// established that the user may do this.
    /// </summary>
    public async Task SetPasswordHashAsync(long id, string passwordHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(SetPasswordSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("hash", passwordHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

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
        r.GetBoolean(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6));
}
