using Npgsql;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>Applies the copied Postgres migration files (in filename order) to a test database.</summary>
public static class Migrations
{
    public static async Task ApplyAllAsync(string connectionString)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "migrations");
        var files = Directory.GetFiles(dir, "*.sql").OrderBy(f => f, StringComparer.Ordinal);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
