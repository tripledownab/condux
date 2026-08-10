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
public sealed class ClickHouseFixture : IAsyncLifetime
{
    private const string User = "condux";
    private const string Password = "condux";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("clickhouse/clickhouse-server:24.8")
        .WithEnvironment("CLICKHOUSE_USER", User)
        .WithEnvironment("CLICKHOUSE_PASSWORD", Password)
        .WithPortBinding(8123, true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(8123).ForPath("/ping")))
        .Build();

    public string BaseUrl => $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8123)}";
    public string ChUser => User;
    public string ChPassword => Password;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await ApplySchemaAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // Apply every ClickHouse migration in filename order, one statement at a time (the HTTP interface
    // runs a single statement per request, unlike clickhouse-client --multiquery).
    private async Task ApplySchemaAsync()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "migrations-clickhouse");
        using var http = new HttpClient();
        foreach (var file in Directory.GetFiles(dir, "*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            await ApplyFileAsync(http, file);
        }
    }

    private async Task ApplyFileAsync(HttpClient http, string file)
    {
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
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/")
            {
                Content = new StringContent(statement),
            };
            req.Headers.Add("X-ClickHouse-User", User);
            req.Headers.Add("X-ClickHouse-Key", Password);
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
}
