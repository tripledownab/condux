using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
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
    private async Task<(HttpClient Client, CapturingFixPublisher Publisher, long ProjectId, string IssuePublicId)>
        ProvisionWithIssueAsync(string fingerprint, int tier = 2)
    {
        var publisher = new CapturingFixPublisher();
        var client = ControlPlaneApp.Create(pg.ConnectionString)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IFixRequestPublisher>(publisher)))
            .CreateClient();

        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, tier);
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
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-1");
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.NotNull(publisher.Job);
        Assert.Equal("acme/api", publisher.Job!.RepoFullName);
        Assert.Equal("main", publisher.Job.BaseBranch); // the linked repo's base branch (defaulted)
    }

    [Fact]
    public async Task RequestFix_WritesThePendingRunBeforeItPublishes()
    {
        // The run exists before the job can reach the worker, so the issue reads as taken from the moment
        // the request is accepted, in every tab and for every admin, and the worker claims that run.
        var publisher = new RunCheckingPublisher(new PostgresFixStore(pg.ConnectionString));
        var (client, _, projectId, issueId) = await SeedFixableIssueAsync(
            s => s.AddSingleton<IFixRequestPublisher>(publisher), "fp-fix-queued-first");

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var queued = Assert.Single(publisher.RunsAtPublish);
        Assert.Equal(FixStatus.Pending, queued.Status);
        // And it is the run the job names, which is what the worker claims.
        Assert.Equal(queued.Id, await new PostgresFixStore(pg.ConnectionString).TryClaimAsync(
            publisher.Job!.ToPendingRun("anthropic", DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task RequestFix_ASecondRequestWhileTheFirstIsQueued_IsRefusedAndRefunded()
    {
        // Before the run was written at request time, the second request reserved and published too, and
        // the worker ran both one after the other, opening two draft PRs for one issue.
        var (client, orgId, projectId, issueId) = await SeedFixableIssueAsync(
            s => s.AddSingleton<IFixRequestPublisher>(new CapturingFixPublisher()), "fp-fix-second-request");
        var first = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("fix_in_progress",
            (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(1, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>(
            $"/api/projects/{projectId}/issues/{issueId}/fixes"))!);
    }

    [Fact]
    public async Task RequestFix_OnARunnerOrg_ASecondRequestWhileTheFirstIsQueued_IsRefusedAndRefunded()
    {
        var (client, orgId, projectId, issueId) = await SeedFixableIssueAsync(
            s => s.AddSingleton<IFixRequestPublisher>(new ThrowingPublisher()), "fp-fix-second-runner");
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 0, costCapUsd: null, fixExecution: (int)FixExecution.Runner);
        var first = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
    }

    // Records the issue's runs as they stood when the job was published.
    private sealed class RunCheckingPublisher(PostgresFixStore fixes) : IFixRequestPublisher
    {
        public FixJob? Job { get; private set; }

        public IReadOnlyList<FixSuggestion> RunsAtPublish { get; private set; } = [];

        public async Task PublishAsync(FixJob job, CancellationToken cancellationToken = default)
        {
            Job = job;
            RunsAtPublish = await fixes.ListByIssueAsync(job.IssueId, cancellationToken);
        }
    }

    [Fact]
    public async Task RequestFix_TargetsTheChosenRepoAndBranch()
    {
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
        // Free (tier 0) runs the Conductor on the same monthly allowance the paid tiers use, just a
        // smaller one. It used to be a one-time lifetime grant, so this asserts the monthly counter is
        // what actually gets spent: the tier's own allowance drives the loop rather than a hardcoded 3.
        var allowance = PlanCatalog.For(Tier.Free).AiFixesPerMonth;
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-free", tier: 0);
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });

        // Each request is for its own issue: an issue holds one run in progress at a time, and this test is
        // about the allowance running out, not that.
        for (var i = 0; i < allowance; i++)
        {
            var target = i == 0 ? issueId : await SeedAnotherIssueAsync(client, projectId);
            var ok = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{target}/fix", new { });
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }
        Assert.NotNull(publisher.Job);

        var over = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/issues/{await SeedAnotherIssueAsync(client, projectId)}/fix", new { });
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal("ai_fix_quota_exceeded",
            (await over.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        // The monthly counter is what moved, not the lifetime one: a Free org that exhausts its month
        // must still read as zero lifetime spend, or a future tier change would inherit a phantom debt.
        Assert.Equal(0, await new PostgresAiFixQuota(pg.ConnectionString).GetLifetimeUsedAsync(
            (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
                .GetProperty("org").GetProperty("id").GetInt64()));
    }

    // A further open issue in the project, by its public id.
    private async Task<string> SeedAnotherIssueAsync(HttpClient client, long projectId)
    {
        var title = $"TypeError: boom {Guid.NewGuid():N}";
        await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", title, "run"), Level.Error, DateTimeOffset.UtcNow);
        var issues = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues"))
            .GetProperty("issues");
        return issues.EnumerateArray().Single(i => i.GetProperty("title").GetString() == title)
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task RequestFix_FreeTier_IsBoundByItsTierDefaultCostCap()
    {
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
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 0, costCapUsd: 10m, fixExecution: (int)FixExecution.Hosted);

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("ai_fix_cost_cap_exceeded",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(publisher.Job);
    }

    [Fact]
    public async Task RequestFix_OnAnOrgThatRunsItsOwnRunner_QueuesForTheRunnerInsteadOfTheTopic()
    {
        // The whole point of slice 4c. If it still published, the hosted Conductor would run a fix the
        // customer chose to keep on their own machines, and their runner would sit idle waiting.
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-runner");
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });
        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 0, costCapUsd: null, fixExecution: (int)FixExecution.Runner);

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Null(publisher.Job); // nothing went to the hosted worker

        // And what it wrote is claimable, carrying the work a runner acts on. The base branch is the
        // assertion that the context round-tripped: it is chosen by the request path, so reading it back
        // off the claim proves the whole object was persisted and handed over, not just the row created.
        // The prompt is empty here only because the assembler reads its sample event from ClickHouse and
        // this suite runs without one; the hosted path is equally empty under the same conditions.
        var job = await new PostgresJobLeaseStore(pg.ConnectionString)
            .TryClaimAsync(orgId, DateTimeOffset.UtcNow);
        Assert.NotNull(job);
        Assert.Equal("acme/api", job!.RepoFullName);
        Assert.Equal("main", job.BaseBranch);
    }

    [Fact]
    public async Task SpendOnTheirOwnRunnerDoesNotUseUpTheirHostedComputeCeiling()
    {
        // The cap governs spend on OUR compute. A run executed on the customer's runner is billed to their
        // model account, so counting it would refuse them hosted runs over money we never paid — and it
        // would report their spend as our cost in the cross-org rollup.
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-runner-spend");
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });
        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping("fp-fix-runner-spend", "TypeError: boom", "run"),
            Level.Error, DateTimeOffset.UtcNow);

        // A finished runner run worth $30 at the priced model, more than the $10 ceiling below.
        var leases = new PostgresJobLeaseStore(pg.ConnectionString);
        var runnerFixId = Guid.CreateVersion7();
        await leases.EnqueueAsync(
            runnerFixId, issue.Id, "acme/api",
            new RunnerJobContext("main", "fix it", []), DateTimeOffset.UtcNow);
        var claimed = await leases.TryClaimAsync(orgId, DateTimeOffset.UtcNow);
        // The claim and the report both name the run's project (the live-badge nudge needs it, ADR-0030).
        Assert.Equal(projectId, claimed!.ProjectId);
        var reported = await leases.TryReportAsync(
            claimed.FixId, claimed.LeaseId, FixStatus.Succeeded, "b", "https://example.invalid/p/1", "s",
            "claude-opus-4-8", 1_000_000, 1_000_000, DateTimeOffset.UtcNow);
        var stored = await new PostgresFixStore(pg.ConnectionString).GetAsync(claimed.FixId);
        Assert.Equal(new ReportedJob(projectId, JobKind.IssueFix), reported);
        Assert.Equal(FixStatus.Succeeded, stored!.Status);

        // Now back on our compute with a $10 ceiling. Their own $30 must not be counted against it.
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 0, costCapUsd: 10m, fixExecution: (int)FixExecution.Hosted);

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.NotNull(publisher.Job);
    }

    [Fact]
    public async Task AFixQueuedForARunnerRecordsWhoAskedForIt()
    {
        // fix_suggestions has no actor column, so the requested audit entry is the only place the
        // triggering user exists. The hosted orchestrator writes it; a run handed to a runner never
        // reaches that orchestrator, so without this the trail starts at "leased" and the user is lost.
        var (client, _, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-audit");
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });
        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 0, costCapUsd: null, fixExecution: (int)FixExecution.Runner);

        await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        var fixes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/issues/{issueId}/fixes");
        var fixId = Guid.Parse(fixes[0].GetProperty("id").GetString()!);
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT actor FROM fix_audit WHERE fix_id = @fix AND event = 'requested';", conn);
        cmd.Parameters.AddWithValue("fix", fixId);

        var actor = await cmd.ExecuteScalarAsync() as string;

        Assert.False(string.IsNullOrEmpty(actor), "the run records nobody as having asked for it");
    }

    [Fact]
    public async Task RequestFix_OnAHostedOrg_IsNeverClaimableByARunner()
    {
        // The mirror of the case above, and the reason job_context gates the claim: a hosted request must
        // stay invisible to a runner even in an org that has one.
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-hosted");
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });
        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.NotNull(publisher.Job);
        Assert.Null(await new PostgresJobLeaseStore(pg.ConnectionString)
            .TryClaimAsync(orgId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SwitchingToYourOwnRunner_NeedsATierThatAllowsIt()
    {
        // Refused rather than ignored: silently staying hosted would leave a customer watching a runner
        // that is never given work, with nothing saying why.
        var (client, _, _, _) = await ProvisionWithIssueAsync("fp-fix-gate", tier: 0);
        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();

        var resp = await client.PatchAsJsonAsync(
            $"/api/orgs/{orgId}", new { aiFixMode = 0, fixExecution = (int)FixExecution.Runner });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("self_hosted_runner_requires_upgrade",
            (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task AFailedRunnerRunRefundsTheAllowanceLikeAFailedHostedRunDoes()
    {
        // ADR-0017: no PR delivered means no allowance spent. The hosted orchestrator refunds in its
        // catch; a runner's failure arrives as a report instead, and the first real runner failure burned
        // its reservation because the report path had no refund.
        var (client, orgId, runner, job) = await LeaseARunnerJobAsync("fp-fix-refund");

        async Task<int> RemainingAsync() =>
            (await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/ai-fix-usage"))
                .GetProperty("remainingFixes").GetInt32();

        var before = await RemainingAsync();
        var report = await ReportAsync(runner, job, FixStatus.Failed, failureReason: null);
        Assert.Equal(HttpStatusCode.NoContent, report.StatusCode);

        Assert.Equal(before + 1, await RemainingAsync()); // the failed run is free again
    }

    [Fact]
    public async Task ARunnerRunQueuedLastMonthDoesNotRefundThisMonthsAllowance()
    {
        // The counter holds one month. A run reserved before it rolled over came out of a month that is
        // gone, so a failure reported now must not hand this month a run it never reserved.
        var (client, orgId, runner, job) = await LeaseARunnerJobAsync("fp-fix-refund-month");
        await using (var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "UPDATE fix_suggestions SET reserved_at = reserved_at - interval '40 days' WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", Guid.Parse(job.GetProperty("fixId").GetString()!));
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        async Task<int> RemainingAsync() =>
            (await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/ai-fix-usage"))
                .GetProperty("remainingFixes").GetInt32();

        var before = await RemainingAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await ReportAsync(runner, job, FixStatus.Failed, null)).StatusCode);

        Assert.Equal(before, await RemainingAsync());
    }

    [Theory]
    [InlineData((int)FixStatus.Failed, (int)FixFailureReason.NoSourceFiles, (int)FixFailureReason.NoSourceFiles)]
    [InlineData((int)FixStatus.Failed, 99, (int)FixFailureReason.Unknown)]
    [InlineData((int)FixStatus.Failed, null, (int)FixFailureReason.Unknown)]
    [InlineData((int)FixStatus.Succeeded, (int)FixFailureReason.NoSourceFiles, null)]
    [InlineData((int)FixStatus.Cancelled, (int)FixFailureReason.NoSourceFiles, null)]
    public async Task ARunnersFailureReasonIsNarrowedToWhatTheDashboardCanExplain(
        int status, int? sent, int? stored)
    {
        // The reason comes from a machine outside our control. A number this version does not know, or
        // none from an older runner, reads as Unknown, and a success never carries one.
        var (client, _, runner, job) = await LeaseARunnerJobAsync("fp-fix-reason-" + status + sent);
        var projectId = job.GetProperty("projectId").GetInt64();
        var fixId = job.GetProperty("fixId").GetString();

        Assert.Equal(HttpStatusCode.NoContent,
            (await ReportAsync(runner, job, (FixStatus)status, (FixFailureReason?)sent)).StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/fixes/{fixId}");
        var reason = detail.GetProperty("failureReason");
        Assert.Equal(stored, reason.ValueKind == JsonValueKind.Null ? null : reason.GetInt32());
        // A failure's summary is the runner's error text, which members are never shown; a cancelled
        // run's is dropped too, since only a success has a summary worth reading.
        Assert.Equal(
            status == (int)FixStatus.Succeeded ? "Guarded the null cart." : "",
            detail.GetProperty("summary").GetString());
    }

    // An org on its own runner, with one fix requested and claimed over the real protocol. The job
    // JSON gains the project id, which the lease response does not carry, for the detail read.
    private async Task<(HttpClient Client, long OrgId, HttpClient Runner, JsonElement Job)> LeaseARunnerJobAsync(
        string fingerprint)
    {
        var (client, _, projectId, issueId) = await ProvisionWithIssueAsync(fingerprint);
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });
        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 0, costCapUsd: null, fixExecution: (int)FixExecution.Runner);
        var mintResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/runner-tokens", new { label = "r" });
        var runnerToken = (await mintResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { })).StatusCode);

        var runner = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        runner.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", runnerToken);
        var lease = await runner.PostAsync("/api/runner/lease", content: null);
        Assert.Equal(HttpStatusCode.OK, lease.StatusCode);
        var job = System.Text.Json.Nodes.JsonNode.Parse(await lease.Content.ReadAsStringAsync())!.AsObject();
        job["projectId"] = projectId;
        return (client, orgId, runner, JsonSerializer.SerializeToElement(job));
    }

    private static Task<HttpResponseMessage> ReportAsync(
        HttpClient runner, JsonElement job, FixStatus status, FixFailureReason? failureReason) =>
        runner.PostAsJsonAsync(
            $"/api/runner/jobs/{job.GetProperty("fixId").GetString()}/result",
            new
            {
                leaseId = job.GetProperty("leaseId").GetString(),
                status = (int)status,
                branch = "",
                prUrl = status == FixStatus.Succeeded ? "https://example.invalid/p/1" : "",
                summary = status == FixStatus.Succeeded
                    ? "Guarded the null cart."
                    : "Anthropic API returned 401: authentication_error: invalid x-api-key",
                model = "",
                inputTokens = 0,
                outputTokens = 0,
                failureReason = (int?)failureReason,
            });

    [Fact]
    public async Task TheExecutionSettingRoundTripsThroughTheOrgListTheDashboardReads()
    {
        // The membership list is what the settings screen renders from. It once hand-picked its org
        // columns, so fix_execution defaulted to hosted no matter what the row said, and the radio
        // snapped back on every click while the server-side setting was in fact saved.
        var (client, _, _, _) = await ProvisionWithIssueAsync("fp-fix-roundtrip");
        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();

        var patch = await client.PatchAsJsonAsync(
            $"/api/orgs/{orgId}", new { aiFixMode = 0, fixExecution = (int)FixExecution.Runner });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        var listed = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0].GetProperty("org");

        Assert.Equal((int)FixExecution.Runner, listed.GetProperty("fixExecution").GetInt32());
    }

    [Fact]
    public async Task AClientThatDoesNotKnowAboutRunnersCannotMoveAnOrgBackToHosted()
    {
        // The dashboard shipped before this field existed and PATCHes only the AI-fix settings. If the
        // field were required, that request would bind 0 and quietly drag a self-hosting org's work back
        // onto our compute as a side effect of changing something unrelated.
        var (client, _, _, _) = await ProvisionWithIssueAsync("fp-fix-omit");
        var orgId = (await client.GetFromJsonAsync<JsonElement>("/api/orgs"))[0]
            .GetProperty("org").GetProperty("id").GetInt64();
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 0, costCapUsd: null, fixExecution: (int)FixExecution.Runner);

        var resp = await client.PatchAsJsonAsync($"/api/orgs/{orgId}", new { aiFixMode = 1 });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var org = await new OrgRepository(pg.ConnectionString).GetAsync(orgId);
        Assert.Equal((int)FixExecution.Runner, org!.FixExecution);
        Assert.Equal(1, org.AiFixMode); // the change it did ask for still landed
    }

    [Fact]
    public async Task RequestFix_NoRepoLinked_Returns409()
    {
        var (client, publisher, projectId, issueId) = await ProvisionWithIssueAsync("fp-fix-2");

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Null(publisher.Job);
    }

    // Fails the way a delivery timeout does, which cannot say whether the broker wrote the job, and keeps
    // the job so a test can deliver it after all.
    private sealed class ThrowingPublisher : IFixRequestPublisher
    {
        public FixJob? Job { get; private set; }

        public Task PublishAsync(FixJob job, CancellationToken cancellationToken = default)
        {
            Job = job;
            throw new InvalidOperationException("broker unreachable");
        }
    }

    [Fact]
    public async Task RequestFix_WhenPublishFails_Returns503AndRefundsTheReservation()
    {
        var publisher = new ThrowingPublisher();
        var (client, orgId, projectId, issueId) = await SeedFixableIssueAsync(
            s => s.AddSingleton<IFixRequestPublisher>(publisher), "fp-fix-publish-fail");

        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fix_enqueue_failed", body.GetProperty("error").GetString());
        // The reservation the request took was refunded, so the failed enqueue burned no allowance.
        var quota = new PostgresAiFixQuota(pg.ConnectionString);
        Assert.Equal(0, await quota.GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        // Its run is cancelled, which frees the issue for another request.
        var runs = await client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{projectId}/issues/{issueId}/fixes");
        Assert.Equal((int)FixStatus.Cancelled, Assert.Single(runs!).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task RequestFix_AJobThatArrivesAfterItsPublishWasReportedFailed_NeitherRunsNorIsRefundedAgain()
    {
        // A delivery timeout cannot say whether the broker wrote the job, so the request refunded it. If it
        // arrives after all, behind a retry the 503 invited, it meets its own run, cancelled, and stops.
        var publisher = new ThrowingPublisher();
        var (client, orgId, projectId, issueId) = await SeedFixableIssueAsync(
            s => s.AddSingleton<IFixRequestPublisher>(publisher), "fp-fix-late-job");
        await client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { });
        var lateJob = publisher.Job!;
        var store = new PostgresFixStore(pg.ConnectionString);
        var retry = (lateJob with { JobId = Guid.CreateVersion7() }).ToPendingRun(provider: "", DateTimeOffset.UtcNow);
        Assert.True(await store.TryQueueAsync(retry));
        await new PostgresAiFixQuota(pg.ConnectionString).TryConsumeAsync(orgId, 25, DateTimeOffset.UtcNow);

        var provider = new CountingProvider();
        Assert.Null(await new FixOrchestrator(store, provider).RunAsync(lateJob));

        Assert.Equal(0, provider.Runs);
        Assert.Equal(1, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        Assert.Equal(FixStatus.Pending, (await store.GetAsync(retry.Id))!.Status);
    }

    private sealed class CountingProvider : IFixProvider
    {
        public int Runs { get; private set; }

        public string Name => "counting";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            Runs++;
            return Task.FromResult(new FixResult("b", "https://example.invalid/p/1", "s"));
        }
    }

    [Fact]
    public async Task RequestFix_WhenTheClientHangsUpDuringPublish_WithdrawsTheRunAndRefunds()
    {
        // A cancelled produce stops the wait, not the send, so the job may still arrive. Its run is cancelled
        // first, so if it does it finds the run no longer pending and does not run: the refund is safe.
        var publisher = new HangingPublisher();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, orgId, projectId, issueId) = await SeedFixableIssueAsync(
            s =>
            {
                s.AddSingleton<IFixRequestPublisher>(publisher);
                s.AddSingleton<IStartupFilter>(new SignalWhenHandled(handled, "/fix"));
            },
            "fp-fix-publish-hangup");
        using var hangUp = new CancellationTokenSource();

        var request = client.PostAsJsonAsync($"/api/projects/{projectId}/issues/{issueId}/fix", new { }, hangUp.Token);
        await publisher.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await hangUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        var runs = await client.GetFromJsonAsync<JsonElement[]>($"/api/projects/{projectId}/issues/{issueId}/fixes");
        Assert.Equal((int)FixStatus.Cancelled, Assert.Single(runs!).GetProperty("status").GetInt32());
    }

    private sealed class HangingPublisher : IFixRequestPublisher
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(FixJob job, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    // A Business org with one project, a linked repo and one open issue, on a control-plane with the
    // given test services.
    private async Task<(HttpClient Client, long OrgId, long ProjectId, string IssueId)> SeedFixableIssueAsync(
        Action<IServiceCollection> services, string fingerprint)
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString)
            .WithWebHostBuilder(b => b.ConfigureTestServices(services))
            .CreateClient();

        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "backend", name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping(fingerprint, "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var issues = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues"))
            .GetProperty("issues");
        var issueId = issues[0].GetProperty("id").GetString()!;
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });
        return (client, orgId, projectId, issueId);
    }

    [Fact]
    public async Task ListFixes_ReturnsSuggestionsForTheIssue()
    {
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
