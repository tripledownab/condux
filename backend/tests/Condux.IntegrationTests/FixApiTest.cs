using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The Conductor request surface (#60/#89): triggering a fix resolves the issue + linked repo and
/// publishes a job; with no repo linked it 409s; suggestions are read back per issue. The Kafka
/// publisher is replaced by a capturing fake, so no broker is needed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FixApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private sealed class CapturingPublisher : IFixRequestPublisher
    {
        public FixJob? Job { get; private set; }

        public Task PublishAsync(FixJob job, CancellationToken cancellationToken = default)
        {
            Job = job;
            return Task.CompletedTask;
        }
    }

    private async Task<(HttpClient Client, CapturingPublisher Publisher, long ProjectId, string IssuePublicId)>
        ProvisionWithIssueAsync(string fingerprint, int tier = 2)
    {
        var publisher = new CapturingPublisher();
        var client = ControlPlaneApp.Create(pg.ConnectionString)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IFixRequestPublisher>(publisher)))
            .CreateClient();

        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme", tier });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();

        // Seed an issue for the project (as the consumer would), then read its public id from the API.
        await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping(fingerprint, "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var issues = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues"))
            .GetProperty("issues");
        var issuePublicId = issues[0].GetProperty("id").GetString()!;

        return (client, publisher, projectId, issuePublicId);
    }

    [Fact]
    public async Task RequestFix_WithLinkedRepo_PublishesJobAndAccepts()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-1");
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.NotNull(publisher.Job);
        Assert.Equal("acme/api", publisher.Job!.RepoFullName);
        Assert.Equal("main", publisher.Job.BaseBranch); // the linked repo's base branch (defaulted)
    }

    [Fact]
    public async Task RequestFix_TargetsTheChosenRepoAndBranch()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-target");
        // Link two repos; the request should target the one it names, on the branch it names — not the
        // first-linked default.
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });
        var second = await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/web" });
        var secondRepoId = (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix",
            new { repoId = secondRepoId, baseBranch = "develop" });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.NotNull(publisher.Job);
        Assert.Equal("acme/web", publisher.Job!.RepoFullName);
        Assert.Equal("develop", publisher.Job.BaseBranch);
    }

    [Fact]
    public async Task RequestFix_WithARepoNotLinkedToTheProject_Returns409()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-badrepo");
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix",
            new { repoId = Guid.NewGuid(), baseBranch = (string?)null });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("repo_not_linked",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(publisher.Job);
    }

    [Fact]
    public async Task RequestFix_FreeTier_SpendsItsMonthlyAllowanceThenExhausts()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        // Free (tier 0) runs the Conductor on the same monthly allowance the paid tiers use, just a
        // smaller one. It used to be a one-time lifetime grant, so this asserts the monthly counter is
        // what actually gets spent: the tier's own allowance drives the loop rather than a hardcoded 3.
        var allowance = PlanCatalog.For(Tier.Free).AiFixesPerMonth;
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-free", tier: 0);
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });

        for (var i = 0; i < allowance; i++)
        {
            var ok = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }
        Assert.NotNull(publisher.Job);

        var over = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal("ai_fix_quota_exceeded",
            (await over.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // The monthly counter is what moved, not the lifetime one: a Free org that exhausts its month
        // must still read as zero lifetime spend, or a future tier change would inherit a phantom debt.
        Assert.Equal(0, await new PostgresAiFixQuota(pg.ConnectionString).GetLifetimeUsedAsync(
            (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
                .GetProperty("org").GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task RequestFix_FreeTier_IsBoundByItsTierDefaultCostCap()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        // No org override anywhere in this test: the point is that the Free tier's OWN default ceiling
        // binds. A run count alone does not bound spend, so a small allowance still needs a dollar stop.
        Assert.NotNull(PlanCatalog.For(Tier.Free).FixComputeCapUsd);
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-free-cap", tier: 0);
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });

        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-free-cap", "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        // 1M in @ $5 + 1M out @ $25 = $30, comfortably past any plausible Free ceiling.
        var run = new FixSuggestion(
            Guid.CreateVersion7(), issue.Id, "acme/api", FixStatus.Succeeded, "anthropic", "claude-opus-4-8",
            "b", "https://example.invalid/p/1", "s", now, now)
        { InputTokens = 1_000_000, OutputTokens = 1_000_000 };
        var fixes = new PostgresFixStore(pg.ConnectionString);
        await fixes.InsertAsync(run);
        await fixes.UpdateAsync(run);

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("ai_fix_cost_cap_exceeded",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(publisher.Job);

        // The cap is checked before the reservation, so a refused request must not have cost the org one
        // of its three runs. Getting this backwards would silently drain a Free org's month.
        Assert.Equal(0, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, now));
    }

    [Fact]
    public async Task RequestFix_OverCostCap_Returns409()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-cap");
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });

        // Resolve the org and seed a completed priced run: 1M in @ $5 + 1M out @ $25 = $30 spend.
        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-fix-cap", "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var run = new FixSuggestion(
            Guid.CreateVersion7(), issue.Id, "acme/api", FixStatus.Succeeded, "anthropic", "claude-opus-4-8",
            "b", "https://example.invalid/p/1", "s", now, now)
        { InputTokens = 1_000_000, OutputTokens = 1_000_000 };
        var fixes = new PostgresFixStore(pg.ConnectionString);
        await fixes.InsertAsync(run);
        await fixes.UpdateAsync(run);

        // Cap at $10 (< $30 already spent) → the next request is refused before reserving a run.
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(orgId, mode: 0, costCapUsd: 10m);

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("ai_fix_cost_cap_exceeded",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(publisher.Job);
    }

    [Fact]
    public async Task RequestFix_NoRepoLinked_Returns409()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-2");

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Null(publisher.Job);
    }

    private sealed class ThrowingPublisher : IFixRequestPublisher
    {
        public Task PublishAsync(FixJob job, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("broker unreachable");
    }

    [Fact]
    public async Task RequestFix_WhenPublishFails_Returns503AndRefundsTheReservation()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = ControlPlaneApp.Create(pg.ConnectionString)
            .WithWebHostBuilder(b => b.ConfigureTestServices(
                s => s.AddSingleton<IFixRequestPublisher>(new ThrowingPublisher())))
            .CreateClient();

        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme", tier = 2 });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-fix-publish-fail", "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var issues = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues"))
            .GetProperty("issues");
        var issueId = issues[0].GetProperty("id").GetString()!;
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fix_enqueue_failed", body.GetProperty("error").GetString());
        // The reservation the request took was refunded, so the failed enqueue burned no allowance.
        var quota = new PostgresAiFixQuota(pg.ConnectionString);
        Assert.Equal(0, await quota.GetUsedAsync(orgId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ListFixes_ReturnsSuggestionsForTheIssue()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (client, _, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-3");

        // Seed a suggestion for the issue's internal id, as the Conductor worker would. Re-upserting the
        // same fingerprint returns the existing issue's internal id.
        var upsert = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-fix-3", "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        await new PostgresFixStore(pg.ConnectionString).InsertAsync(new FixSuggestion(
            Guid.CreateVersion7(), upsert.Id, "acme/api", FixStatus.Succeeded, "fake", "claude-opus-4-8",
            "condux/fix-1", "https://example.invalid/acme/api/pull/0", "draft", now, now));

        var fixes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issueId}/fixes");

        Assert.True(fixes.GetArrayLength() >= 1);
        Assert.Equal("acme/api", fixes[0].GetProperty("repoFullName").GetString());
        Assert.Equal((int)FixStatus.Succeeded, fixes[0].GetProperty("status").GetInt32());
    }
}
