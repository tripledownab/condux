using System.Linq;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// Ephemeral ClickHouse container (one per test class) with the events schema applied.
/// Uses the same condux/condux credentials the compose stack does, and reaches the
/// server over its HTTP interface (port 8123) — the same interface the writer/reader use.
/// </summary>
public class ClickHouseFixture : IAsyncLifetime
{
    private const string User = "condux";
    private const string Password = "condux";

    private readonly IContainer _container;

    public ClickHouseFixture()
        : this(null)
    {
    }

    /// <summary>A server whose own timezone is <paramref name="timezone"/>; null keeps the image's UTC.</summary>
    protected ClickHouseFixture(string? timezone)
    {
        var builder = new ContainerBuilder(ComposeImages.For("clickhouse/clickhouse-server"))
            .WithEnvironment("CLICKHOUSE_USER", User)
            .WithEnvironment("CLICKHOUSE_PASSWORD", Password)
            .WithPortBinding(8123, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8123).ForPath("/ping")));
        _container = (timezone is null ? builder : builder.WithEnvironment("TZ", timezone)).Build();
    }

    public string BaseUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8123)}";
    public string ChUser => User;
    public string ChPassword => Password;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplySchemaAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Every ClickHouse migration, in the filename order every deploy applies them in.</summary>
    public static IReadOnlyList<string> MigrationFiles() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "migrations-clickhouse"), "*.sql")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

    private async Task ApplySchemaAsync()
    {
        foreach (var file in MigrationFiles())
        {
            await ApplyMigrationAsync(file);
        }
    }

    /// <summary>Applies one migration file, one statement at a time (the HTTP interface runs a single
    /// statement per request, unlike clickhouse-client --multiquery).</summary>
    public async Task ApplyMigrationAsync(string file)
    {
        using var http = new HttpClient();
        var sql = await File.ReadAllTextAsync(file);

        // Strip `--` comments first: the migrations have semicolons inside comments, so splitting the raw
        // text on ';' would yield comment-only (empty) statements.
        var stripped = string.Join('\n', sql.Split('\n').Select(line =>
        {
            var idx = line.IndexOf("--", StringComparison.Ordinal);
            return idx >= 0 ? line[..idx] : line;
        }));

        foreach (var statement in stripped.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var req = Request(statement);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"ClickHouse migration failed ({(int)resp.StatusCode}) in {Path.GetFileName(file)}: {body}"
                    + $"\n--- statement ---\n{statement}");
            }
        }
    }

    /// <summary>Runs one statement and returns its answer as text, failing with the server's error.</summary>
    public async Task<string> QueryAsync(string sql)
    {
        using var http = new HttpClient();
        using var req = Request(sql);
        using var resp = await http.SendAsync(req);
        await resp.EnsureSuccessAsync();
        return await resp.Content.ReadAsStringAsync();
    }

    private HttpRequestMessage Request(string sql)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/") { Content = new StringContent(sql) };
        req.Headers.Add("X-ClickHouse-User", User);
        req.Headers.Add("X-ClickHouse-Key", Password);
        return req;
    }
}
