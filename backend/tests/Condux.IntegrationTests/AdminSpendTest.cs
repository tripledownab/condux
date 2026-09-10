using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Cross-org Conductor spend (ADR-0027). Seeds issue fixes + CVE bumps across two orgs with a priced
/// (Anthropic) and a bring-your-own (unpriced) model, then checks the rollup sums only priced tokens, the
/// per-model / per-org breakdowns are right, and the per-run drill-down lists both kinds (including a
/// failed 0-token run with null cost) and honors its filters. Runner-executed runs of both kinds are in
/// the drill-down but out of every total — their tokens are the customer's own model bill (ADR-0033).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminSpendTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string AdminEmail = "boss@condux.test";
    private const string Priced = "claude-opus-4-8"; // $5 / $25 per 1M input / output
    private const string Byo = "llama-3.3-70b"; // self-hosted BYO, not in ModelPricing -> cost null

    [Fact]
    public async Task Rollup_prices_only_known_models_and_the_drilldown_lists_every_run()
    {
        var cs = pg.ConnectionString;

        // Org A: a priced issue fix ($30), a BYO issue fix (tokens, no price), a priced CVE bump ($2.50),
        // and a failed 0-token run (in the drill-down, out of the rollup).
        var (orgA, projectA) = await SeedOrgProjectAsync(cs, "A");
        await SeedIssueFixAsync(cs, projectA, Priced, 1_000_000, 1_000_000, status: 3);
        await SeedIssueFixAsync(cs, projectA, Byo, 2_000_000, 0, status: 3);
        await SeedCveFixAsync(cs, projectA, Priced, 500_000, 0, status: 3);
        await SeedIssueFixAsync(cs, projectA, Priced, 0, 0, status: 4);
        // Runner-executed runs of both kinds (they carry a job_context): billed to the customer's own
        // model account, so their tokens must not move any spend total below — only the drill-down
        // lists them (ADR-0033 slice 4c).
        await SeedIssueFixAsync(cs, projectA, Priced, 2_000_000, 2_000_000, status: 3, runner: true);
        await SeedCveFixAsync(cs, projectA, Priced, 1_000_000, 0, status: 3, runner: true);
        // Org B: one priced issue fix ($5).
        var (_, projectB) = await SeedOrgProjectAsync(cs, "B");
        await SeedIssueFixAsync(cs, projectB, Priced, 1_000_000, 0, status: 3);

        var app = ControlPlaneApp.Create(cs, platformAdminEmails: AdminEmail);
        var admin = app.CreateClient();
        await ApiAuth.SignUpAsync(admin, AdminEmail);

        // Cross-org rollup: total sums only priced tokens (30 + 2.50 + 5 = 37.50); BYO tokens show, $0.
        var rollup = await admin.GetFromJsonAsync<JsonElement>("/api/admin/spend?days=30");
        Assert.Equal(37.50m, rollup.GetProperty("totalUsd").GetDecimal());
        var byModel = rollup.GetProperty("byModel").EnumerateArray().ToList();
        var opus = byModel.First(m => m.GetProperty("model").GetString() == Priced);
        Assert.Equal(37.50m, opus.GetProperty("costUsd").GetDecimal());
        var byo = byModel.First(m => m.GetProperty("model").GetString() == Byo);
        Assert.Equal(JsonValueKind.Null, byo.GetProperty("costUsd").ValueKind);
        Assert.Equal(2_000_000, byo.GetProperty("inputTokens").GetInt64());

        // Single-org rollup for A excludes B's $5.
        var orgSpend = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/orgs/{orgA}/spend?days=30");
        Assert.Equal(32.50m, orgSpend.GetProperty("totalUsd").GetDecimal());

        // Drill-down lists all runs, including the failed 0-token one and the two runner-executed ones
        // (kept, unlike the rollup) and the BYO run whose unpriced model yields a null cost.
        var runs = await admin.GetFromJsonAsync<JsonElement>("/api/admin/spend/runs?days=30");
        Assert.Equal(7, runs.GetArrayLength());
        Assert.Contains(runs.EnumerateArray(), r => r.GetProperty("status").GetInt32() == 4);
        Assert.Contains(runs.EnumerateArray(), r =>
            r.GetProperty("model").GetString() == Byo && r.GetProperty("costUsd").ValueKind == JsonValueKind.Null);

        // Filters: kind = cve_bump returns only the bump; orgId = A excludes B's runs.
        var cveRuns = await admin.GetFromJsonAsync<JsonElement>("/api/admin/spend/runs?days=30&kind=cve_bump");
        Assert.All(cveRuns.EnumerateArray(), r => Assert.Equal("cve_bump", r.GetProperty("kind").GetString()));
        Assert.NotEmpty(cveRuns.EnumerateArray());
        var aRuns = await admin.GetFromJsonAsync<JsonElement>($"/api/admin/spend/runs?days=30&orgId={orgA}");
        Assert.All(aRuns.EnumerateArray(), r => Assert.Equal(orgA, r.GetProperty("orgId").GetInt64()));
    }

    private static async Task<(long OrgId, long ProjectId)> SeedOrgProjectAsync(string cs, string tag)
    {
        var org = await new OrgRepository(cs).CreateAsync($"org-{tag}-{Guid.NewGuid():N}", $"Org {tag}", 0);
        var project = await new ProjectRepository(cs).CreateAsync(org.Id, $"Project {tag}", "other");
        return (org.Id, project.Id);
    }

    private static async Task SeedIssueFixAsync(
        string cs, long projectId, string model, long input, long output, int status, bool runner = false)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        var issueId = await ScalarAsync<long>(conn,
            "INSERT INTO issues (project_id, fingerprint, title) VALUES (@p, @f, 'x') RETURNING id;",
            ("p", projectId), ("f", Guid.NewGuid().ToString("N")));
        await ExecAsync(conn,
            """
            INSERT INTO fix_suggestions
                (id, issue_id, repo_full_name, status, model, input_tokens, output_tokens, job_context)
            VALUES (@id, @issue, 'acme/app', @status, @model, @in, @out, @ctx::jsonb);
            """,
            ("id", Guid.NewGuid()), ("issue", issueId), ("status", (short)status),
            ("model", model), ("in", input), ("out", output),
            ("ctx", runner ? "{}" : DBNull.Value));
    }

    private static async Task SeedCveFixAsync(
        string cs, long projectId, string model, long input, long output, int status, bool runner = false)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        // Unique per call: repo_links is UNIQUE (project_id, repo_full_name), and a project seeds
        // several bumps here.
        var repoLinkId = Guid.NewGuid();
        await ExecAsync(conn,
            "INSERT INTO repo_links (id, project_id, repo_full_name) VALUES (@id, @p, @repo);",
            ("id", repoLinkId), ("p", projectId), ("repo", $"acme/app-{repoLinkId:N}"));
        await ExecAsync(conn,
            """
            INSERT INTO cve_fix_runs
                (id, repo_link_id, ghsa_id, package, ecosystem, from_range, to_version, status, model,
                 input_tokens, output_tokens, job_context)
            VALUES (@id, @repo, 'GHSA-x', 'left-pad', 'npm', '<1.0', '1.0', @status, @model, @in, @out,
                    @ctx::jsonb);
            """,
            ("id", Guid.NewGuid()), ("repo", repoLinkId), ("status", (short)status),
            ("model", model), ("in", input), ("out", output),
            ("ctx", runner ? "{}" : DBNull.Value));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql, params (string, object)[] ps)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in ps)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
