using Npgsql;

namespace Condux.Storage.Postgres;

public sealed record SavedView(long Id, string Name, string Query, string Sort);

/// <summary>A user's saved issue views for one project: named search + ordering presets.</summary>
public sealed class SavedViewRepository(string connectionString)
{
    private const string ListSql = """
        SELECT id, name, query, sort FROM saved_views
        WHERE user_id = @user AND project_id = @project
        ORDER BY created_at;
        """;

    private const string InsertSql = """
        INSERT INTO saved_views (user_id, project_id, name, query, sort)
        VALUES (@user, @project, @name, @query, @sort)
        ON CONFLICT (user_id, project_id, name) DO UPDATE SET query = @query, sort = @sort
        RETURNING id, name, query, sort;
        """;

    private const string UpdateSql = """
        UPDATE saved_views SET name = @name, query = @query, sort = @sort
        WHERE id = @id AND user_id = @user AND project_id = @project;
        """;

    private const string DeleteSql = """
        DELETE FROM saved_views WHERE id = @id AND user_id = @user AND project_id = @project;
        """;

    public async Task<IReadOnlyList<SavedView>> ListAsync(
        long userId, long projectId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("project", projectId);
        var views = new List<SavedView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            views.Add(Read(reader));
        }
        return views;
    }

    /// <summary>Create a view; saving an existing name overwrites that view (save-as semantics).</summary>
    public async Task<SavedView> UpsertAsync(
        long userId, long projectId, string name, string query, string sort, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("sort", sort);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    /// <summary>Rewrite a view (rename and/or new query/sort). False when it is not the caller's.</summary>
    public async Task<bool> UpdateAsync(
        long id, long userId, long projectId, string name, string query, string sort,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(UpdateSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("sort", sort);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> DeleteAsync(long id, long userId, long projectId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(DeleteSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("project", projectId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static SavedView Read(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3));
}
