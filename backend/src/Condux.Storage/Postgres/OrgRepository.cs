using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>An organization (billing + tenancy boundary). Tier matches Condux.Core.Plans.Tier;
/// AiFixMode matches Condux.Core.FixEngine.AiFixMode (0 manual, 1 auto — #101). AiFixCostCapUsd is the
/// optional monthly Conductor spend ceiling in USD (null = no cap, #120 budgets). The WeeklySummary* fields
/// are the per-org weekly digest schedule (ADR-0031): Dow is a .NET DayOfWeek (0=Sun..6=Sat), Hour a local
/// hour, Tz an IANA zone id. FixExecution matches Condux.Core.FixEngine.FixExecution (0 hosted, 1 the
/// org's own runner — ADR-0033 slice 4c).</summary>
public sealed record Org(
    long Id, string Slug, string Name, int Tier, DateTimeOffset CreatedAt, int AiFixMode, decimal? AiFixCostCapUsd,
    string? StripeCustomerId = null, string? StripeSubscriptionId = null,
    bool WeeklySummaryEnabled = true, int WeeklySummaryDow = 1, int WeeklySummaryHour = 9,
    string WeeklySummaryTz = "UTC", int FixExecution = 0);

/// <summary>An org whose weekly digest is enabled, with just the fields the send worker needs (ADR-0031).</summary>
public readonly record struct WeeklySummaryOrg(long Id, string Name, int Dow, int Hour, string Tz);

/// <summary>Creates and reads organizations.</summary>
public sealed class OrgRepository(string connectionString)
{
    // Internal, together with Read below, so a query that joins orgs elsewhere selects THIS list rather
    // than hand-picking columns. A hand-built Org silently defaults every field it forgot: the membership
    // list did exactly that, so the dashboard saw fix_execution as hosted no matter what the row said,
    // and the settings radio snapped back on every click.
    internal const string Columns =
        "id, slug, name, tier, created_at, ai_fix_mode, ai_fix_cost_cap_usd, stripe_customer_id, stripe_subscription_id, "
        + "weekly_summary_enabled, weekly_summary_dow, weekly_summary_hour, weekly_summary_tz, fix_execution";

    /// <summary>The Columns list qualified for a join, e.g. <c>o.id, o.slug, ...</c>.</summary>
    internal static string QualifiedColumns(string alias) =>
        string.Join(", ", Columns.Split(", ").Select(column => $"{alias}.{column}"));

    private const string InsertSql = $"""
        INSERT INTO orgs (slug, name, tier)
        VALUES (@slug, @name, @tier)
        RETURNING {Columns};
        """;

    private const string GetSql = $"SELECT {Columns} FROM orgs WHERE id = @id;";

    // One atomic update of the org's AI-fix settings (mode, cost cap, and where runs execute); the
    // Settings form submits them together.
    private const string UpdateSettingsSql = $"""
        UPDATE orgs SET ai_fix_mode = @mode, ai_fix_cost_cap_usd = @cap, fix_execution = @execution
        WHERE id = @id
        RETURNING {Columns};
        """;

    private const string RenameSql = $"UPDATE orgs SET name = @name WHERE id = @id RETURNING {Columns};";

    public async Task<Org> CreateAsync(string slug, string name, int tier, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("slug", slug);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("tier", (short)tier);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    /// <summary>Delete an org; every child table cascades. Used when a solo personal org is
    /// abandoned for an invite (ADR-0018). False if not found.</summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM orgs WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<Org?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(GetSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Set the org's AI-fix settings — mode (#101) and cost cap (#120, null clears it). Returns
    /// the updated org, or null if it no longer exists.</summary>
    public async Task<Org?> UpdateSettingsAsync(
        long id, int mode, decimal? costCapUsd, int fixExecution, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(UpdateSettingsSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("mode", (short)mode);
        cmd.Parameters.AddWithValue("cap", (object?)costCapUsd ?? DBNull.Value);
        cmd.Parameters.AddWithValue("execution", (short)fixExecution);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Rename an org (platform-admin edit, ADR-0027). Returns the updated org, or null if it no
    /// longer exists. The org name is display-only; the slug and tier are left untouched.</summary>
    public async Task<Org?> RenameAsync(long id, string name, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RenameSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", name);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Links an org to the Stripe customer + subscription created by a completed checkout.</summary>
    public async Task LinkStripeCustomerAsync(
        long orgId, string customerId, string? subscriptionId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE orgs SET stripe_customer_id = @c, stripe_subscription_id = @s WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", orgId);
        cmd.Parameters.AddWithValue("c", customerId);
        cmd.Parameters.AddWithValue("s", (object?)subscriptionId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Sets the tier for the org owning <paramref name="customerId"/> (its subscription became/stayed
    /// active). Returns true if an org matched. Idempotent — safe to re-apply on webhook redelivery.
    /// </summary>
    public async Task<bool> ApplyTierByStripeCustomerAsync(
        string customerId, int tier, string? subscriptionId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE orgs SET tier = @t, stripe_subscription_id = @s WHERE stripe_customer_id = @c;", conn);
        cmd.Parameters.AddWithValue("t", (short)tier);
        cmd.Parameters.AddWithValue("s", (object?)subscriptionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("c", customerId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Downgrades the org owning <paramref name="customerId"/> to Free and clears its subscription
    /// (the subscription was canceled). Returns true if an org matched.</summary>
    public async Task<bool> DowngradeByStripeCustomerAsync(string customerId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE orgs SET tier = 0, stripe_subscription_id = NULL WHERE stripe_customer_id = @c;", conn);
        cmd.Parameters.AddWithValue("c", customerId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Every org whose weekly summary is enabled, with the schedule the send worker needs.</summary>
    public async Task<IReadOnlyList<WeeklySummaryOrg>> ListWeeklySummaryEnabledAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, name, weekly_summary_dow, weekly_summary_hour, weekly_summary_tz FROM orgs "
            + "WHERE weekly_summary_enabled = true ORDER BY id;", conn);
        var list = new List<WeeklySummaryOrg>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new WeeklySummaryOrg(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt16(2), reader.GetInt16(3), reader.GetString(4)));
        }
        return list;
    }

    /// <summary>Set the org's weekly-summary enable flag + schedule (ADR-0031). Returns the updated org, or
    /// null if it no longer exists. The caller validates the day/hour/tz ranges.</summary>
    public async Task<Org?> UpdateWeeklySummaryAsync(
        long id, bool enabled, int dow, int hour, string timezone, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE orgs SET weekly_summary_enabled = @enabled, weekly_summary_dow = @dow,
                weekly_summary_hour = @hour, weekly_summary_tz = @tz
            WHERE id = @id
            RETURNING {Columns};
            """, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("enabled", enabled);
        cmd.Parameters.AddWithValue("dow", (short)dow);
        cmd.Parameters.AddWithValue("hour", (short)hour);
        cmd.Parameters.AddWithValue("tz", timezone);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Maps one row of <see cref="Columns"/> (which must lead the select list) to an Org.</summary>
    internal static Org Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt16(3),
        r.GetFieldValue<DateTimeOffset>(4), r.GetInt16(5),
        r.IsDBNull(6) ? null : r.GetDecimal(6),
        r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetBoolean(9), r.GetInt16(10), r.GetInt16(11), r.GetString(12), r.GetInt16(13));
}
