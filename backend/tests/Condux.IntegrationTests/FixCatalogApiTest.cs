using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The project-wide Fixes section (#118): list joins issue context, detail carries the audit trail,
/// marking viewed clears it from the needs-attention badge, and archive hides a fix from the active
/// list while keeping it under ?archived=true. Tenancy is scoped by project on every query.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FixCatalogApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Fix_catalog_lists_reads_views_and_archives()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();

        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-catalog", "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);

        // A succeeded run with a draft PR (needs attention) + an audit entry.
        var fixes = new PostgresFixStore(pg.ConnectionString);
        var fixId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await fixes.InsertAsync(new FixSuggestion(
            fixId, issue.Id, "acme/api", FixStatus.Succeeded, "fake", "claude-opus-4-8",
            "condux/fix-1", "https://example.invalid/acme/api/pull/1", "guards the null deref", now, now));
        await fixes.AppendAuditAsync(fixId, "conductor", "draft_pr_opened", """{"branch":"condux/fix-1"}""");

        // The list carries issue context and starts unviewed; the badge counts it.
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/fixes");
        var row = Assert.Single(list.EnumerateArray());
        Assert.Equal("TypeError: boom", row.GetProperty("issueTitle").GetString());
        Assert.Equal(issue.PublicId, row.GetProperty("issuePublicId").GetGuid());
        Assert.False(row.GetProperty("viewed").GetBoolean());
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/fixes/unviewed-count")).GetProperty("count").GetInt32());

        // Detail returns the audit trail.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/fixes/{fixId}");
        Assert.Equal("guards the null deref", detail.GetProperty("summary").GetString());
        var entries = detail.GetProperty("audit").EnumerateArray().ToList();
        Assert.Contains(entries, a => a.GetProperty("event").GetString() == "draft_pr_opened");

        // The trail says what happened and never carries the row's detail JSON. That column is written by
        // six places with no shared rule about what may go in it, and this endpoint is member-level, so
        // an entry that carried it would publish whatever the newest writer happened to put there. The
        // NotEmpty guards the assertion itself: Assert.All over an empty list passes and proves nothing.
        Assert.NotEmpty(entries);
        Assert.All(entries, a =>
        {
            Assert.False(a.TryGetProperty("detail", out _));
            Assert.True(a.TryGetProperty("event", out _));
        });

        // Viewing clears it from the badge.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/projects/{projectId}/fixes/{fixId}/view", null)).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/fixes/unviewed-count")).GetProperty("count").GetInt32());

        // Archiving removes it from the active list but keeps it under ?archived=true.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PatchAsJsonAsync(
            $"/api/projects/{projectId}/fixes/{fixId}", new { archived = true })).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/fixes")).EnumerateArray());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/fixes?archived=true")).EnumerateArray());
    }

    [Fact]
    public async Task Fix_cost_rollup_prices_priced_models_and_leaves_byo_uncounted()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();

        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-cost", "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var fixes = new PostgresFixStore(pg.ConnectionString);
        var now = DateTimeOffset.UtcNow;

        // A priced Opus run (1M in @ $5 + 0.5M out @ $25 = $17.50). Insert then update, as the
        // orchestrator does, since token usage is written on the success update.
        var opus = new FixSuggestion(
            Guid.CreateVersion7(), issue.Id, "acme/api", FixStatus.Succeeded, "anthropic", "claude-opus-4-8",
            "condux/fix-1", "https://example.invalid/p/1", "s", now, now)
        { InputTokens = 1_000_000, OutputTokens = 500_000 };
        await fixes.InsertAsync(opus);
        await fixes.UpdateAsync(opus);

        // A bring-your-own model run on a self-hosted model not in ModelPricing: tokens count, cost null.
        var byo = new FixSuggestion(
            Guid.CreateVersion7(), issue.Id, "acme/api", FixStatus.Succeeded, "openai-compat", "llama-3.3-70b",
            "condux/fix-2", "https://example.invalid/p/2", "s", now, now)
        { InputTokens = 200_000, OutputTokens = 100_000 };
        await fixes.InsertAsync(byo);
        await fixes.UpdateAsync(byo);

        // A no-model fake run (0 tokens) never incurred cost, so it is excluded from the spend rollup.
        await fixes.InsertAsync(new FixSuggestion(
            Guid.CreateVersion7(), issue.Id, "acme/api", FixStatus.Succeeded, "fake", "claude-opus-4-8",
            "condux/fix-3", "https://example.invalid/p/3", "s", now, now));

        var rollup = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/fixes/cost");
        Assert.Equal(17.50m, rollup.GetProperty("totalUsd").GetDecimal()); // only the priced Opus run
        Assert.Equal(2, rollup.GetProperty("runCount").GetInt32()); // opus + byo; the 0-token fake dropped
        Assert.Equal(1_200_000, rollup.GetProperty("inputTokens").GetInt64());

        var models = rollup.GetProperty("byModel").EnumerateArray().ToList();
        var opusRow = models.Single(m => m.GetProperty("model").GetString() == "claude-opus-4-8");
        Assert.Equal(17.50m, opusRow.GetProperty("costUsd").GetDecimal());
        var byoRow = models.Single(m => m.GetProperty("model").GetString() == "llama-3.3-70b");
        Assert.Equal(JsonValueKind.Null, byoRow.GetProperty("costUsd").ValueKind);
        Assert.Equal(300_000, byoRow.GetProperty("inputTokens").GetInt64() + byoRow.GetProperty("outputTokens").GetInt64());

        // The per-fix detail carries the same derived cost.
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/fixes/{opus.Id}");
        Assert.Equal(17.50m, detail.GetProperty("costUsd").GetDecimal());
        Assert.Equal(1_000_000, detail.GetProperty("inputTokens").GetInt64());
    }

    [Fact]
    public async Task Fixes_of_another_project_are_not_visible()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);

        async Task<long> ProjectAsync(string slug)
        {
            var r = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
                new { slug, name = slug, platform = "python" });
            return (await r.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("project").GetProperty("id").GetInt64();
        }

        var projectA = await ProjectAsync("a");
        var projectB = await ProjectAsync("b");
        var issueA = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectA, new Grouping("fp-a", "A boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        await new PostgresFixStore(pg.ConnectionString).InsertAsync(new FixSuggestion(
            Guid.CreateVersion7(), issueA.Id, "acme/a", FixStatus.Succeeded, "fake", "m",
            "b", "https://example.invalid/p/1", "s", now, now));

        Assert.Single((await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectA}/fixes")).EnumerateArray());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectB}/fixes")).EnumerateArray());
    }
}
