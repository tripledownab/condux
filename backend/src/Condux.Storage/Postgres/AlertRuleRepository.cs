using Condux.Core.Alerting;
using Condux.Core.Events;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Alert rules and their notification channels, in Postgres (#54). Rule reads are scoped by
/// project id so a tenant can only touch its own; the engine loads enabled rules with their channels.</summary>
public sealed class AlertRuleRepository(string connectionString)
{
    private const string CreateRuleSql = """
        INSERT INTO alert_rules (id, project_id, name, events, levels, enabled)
        VALUES (@id, @project, @name, @events, @levels, TRUE)
        RETURNING id, project_id, name, events, levels, enabled;
        """;

    private const string ListRulesSql = """
        SELECT id, project_id, name, events, levels, enabled
        FROM alert_rules WHERE project_id = @project ORDER BY created_at;
        """;

    private const string GetRuleSql = """
        SELECT id, project_id, name, events, levels, enabled
        FROM alert_rules WHERE project_id = @project AND id = @id;
        """;

    private const string UpdateRuleSql = """
        UPDATE alert_rules
        SET name = @name, events = @events, levels = @levels, enabled = @enabled
        WHERE project_id = @project AND id = @id;
        """;

    private const string UpdateChannelSql =
        "UPDATE alert_channels SET channel = @channel, target = @target, template = @template WHERE rule_id = @rule AND id = @id;";

    private const string DeleteRuleSql = "DELETE FROM alert_rules WHERE project_id = @project AND id = @id;";

    private const string AddChannelSql = """
        INSERT INTO alert_channels (id, rule_id, channel, target, template)
        VALUES (@id, @rule, @channel, @target, @template)
        RETURNING id, rule_id, channel, target, template;
        """;

    private const string ListChannelsSql =
        "SELECT id, rule_id, channel, target, template FROM alert_channels WHERE rule_id = @rule ORDER BY created_at;";

    private const string DeleteChannelSql = "DELETE FROM alert_channels WHERE rule_id = @rule AND id = @id;";

    // Enabled rules for a project joined to their channels; a rule with no channel is skipped (nothing
    // to deliver). Grouped into AlertRuleWithChannels in memory.
    private const string EnabledWithChannelsSql = """
        SELECT r.id, r.project_id, r.name, r.events, r.levels, r.enabled,
               c.id, c.rule_id, c.channel, c.target, c.template
        FROM alert_rules r
        JOIN alert_channels c ON c.rule_id = r.id
        WHERE r.project_id = @project AND r.enabled = TRUE
        ORDER BY r.created_at, c.created_at;
        """;

    // Every rule for a project (enabled or not) with its channels; a channel-less rule still appears
    // (LEFT JOIN) so the management UI can show and attach channels to it.
    private const string AllWithChannelsSql = """
        SELECT r.id, r.project_id, r.name, r.events, r.levels, r.enabled,
               c.id, c.rule_id, c.channel, c.target, c.template
        FROM alert_rules r
        LEFT JOIN alert_channels c ON c.rule_id = r.id
        WHERE r.project_id = @project
        ORDER BY r.created_at, c.created_at;
        """;

    public async Task<AlertRule> CreateRuleAsync(
        long projectId, string name, IReadOnlyList<AlertEventType> events, IReadOnlyList<Level> levels,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(CreateRuleSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("events", events.Select(e => (short)e).ToArray());
        cmd.Parameters.AddWithValue("levels", levels.Select(level => (short)level).ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return MapRule(reader);
    }

    public async Task<IReadOnlyList<AlertRule>> ListRulesByProjectAsync(
        long projectId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListRulesSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        var list = new List<AlertRule>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapRule(reader));
        }
        return list;
    }

    /// <summary>A rule by id, scoped to its project (tenancy). Null if absent or owned by another project.</summary>
    public async Task<AlertRule?> GetRuleAsync(
        long projectId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(GetRuleSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRule(reader) : null;
    }

    /// <summary>Update a rule's settings (name, events, levels, enabled); false when none matched for the
    /// project. The enable/disable toggle and the edit form both go through this.</summary>
    public async Task<bool> UpdateRuleAsync(
        long projectId, Guid id, string name, IReadOnlyList<AlertEventType> events, IReadOnlyList<Level> levels,
        bool enabled, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UpdateRuleSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("events", events.Select(e => (short)e).ToArray());
        cmd.Parameters.AddWithValue("levels", levels.Select(level => (short)level).ToArray());
        cmd.Parameters.AddWithValue("enabled", enabled);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Delete a rule (its channels cascade); returns false when none matched.</summary>
    public async Task<bool> DeleteRuleAsync(long projectId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(DeleteRuleSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<AlertChannel> AddChannelAsync(
        Guid ruleId, NotificationChannel channel, string target, string? template = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(AddChannelSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("rule", ruleId);
        cmd.Parameters.AddWithValue("channel", (short)channel);
        cmd.Parameters.AddWithValue("target", target);
        cmd.Parameters.AddWithValue("template", (object?)template ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return MapChannel(reader);
    }

    public async Task<IReadOnlyList<AlertChannel>> ListChannelsByRuleAsync(
        Guid ruleId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListChannelsSql, conn);
        cmd.Parameters.AddWithValue("rule", ruleId);
        var list = new List<AlertChannel>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapChannel(reader));
        }
        return list;
    }

    public async Task<bool> DeleteChannelAsync(
        Guid ruleId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(DeleteChannelSql, conn);
        cmd.Parameters.AddWithValue("rule", ruleId);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Update a channel's type + target; false when none matched. Keyed on rule id (the endpoint
    /// scopes the rule to its project first, so tenancy holds).</summary>
    public async Task<bool> UpdateChannelAsync(
        Guid ruleId, Guid id, NotificationChannel channel, string target, string? template = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UpdateChannelSql, conn);
        cmd.Parameters.AddWithValue("rule", ruleId);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("channel", (short)channel);
        cmd.Parameters.AddWithValue("target", target);
        cmd.Parameters.AddWithValue("template", (object?)template ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Enabled rules for a project with their channels, as the engine dispatches them.</summary>
    public async Task<IReadOnlyList<AlertRuleWithChannels>> ListEnabledWithChannelsByProjectAsync(
        long projectId, CancellationToken cancellationToken = default) =>
        await GroupWithChannelsAsync(EnabledWithChannelsSql, projectId, cancellationToken);

    /// <summary>Every rule for a project with its channels (the management UI's view). Includes disabled
    /// and channel-less rules.</summary>
    public async Task<IReadOnlyList<AlertRuleWithChannels>> ListWithChannelsByProjectAsync(
        long projectId, CancellationToken cancellationToken = default) =>
        await GroupWithChannelsAsync(AllWithChannelsSql, projectId, cancellationToken);

    // Runs a "rules joined to channels" query and groups the flat rows into AlertRuleWithChannels. A
    // LEFT JOIN row for a channel-less rule has NULL channel columns (guarded here), so this serves both
    // the inner-join (enabled) and left-join (all) queries.
    private async Task<IReadOnlyList<AlertRuleWithChannels>> GroupWithChannelsAsync(
        string sql, long projectId, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("project", projectId);

        var rules = new List<AlertRule>();
        var channelsByRule = new Dictionary<Guid, List<AlertChannel>>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rule = MapRule(reader);
            if (!channelsByRule.TryGetValue(rule.Id, out var channels))
            {
                channels = [];
                channelsByRule[rule.Id] = channels;
                rules.Add(rule);
            }
            if (!await reader.IsDBNullAsync(6, cancellationToken))
            {
                channels.Add(new AlertChannel(
                    reader.GetGuid(6), reader.GetGuid(7), (NotificationChannel)reader.GetInt16(8),
                    reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
        }
        return [.. rules.Select(rule => new AlertRuleWithChannels(rule, channelsByRule[rule.Id]))];
    }

    private static AlertRule MapRule(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt64(1), r.GetString(2),
        [.. r.GetFieldValue<short[]>(3).Select(e => (AlertEventType)e)],
        [.. r.GetFieldValue<short[]>(4).Select(level => (Level)level)], r.GetBoolean(5));

    private static AlertChannel MapChannel(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), (NotificationChannel)r.GetInt16(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4));
}
