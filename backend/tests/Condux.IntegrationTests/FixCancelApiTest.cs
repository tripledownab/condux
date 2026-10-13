using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.CveFix;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Cancelling a fix run nothing executes: pending, or a runner's whose lease lapsed. A run queued for a
/// runner is concluded only by that runner, and one in progress per issue or advisory is all the database
/// admits, so a runner that never came, or died holding its lease, held its issue with a 409 for good. These drive the real endpoints against real Postgres, and check the
/// allowance and what the other consumers of the row (the lease, the conductor's claim) do afterwards.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FixCancelApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private sealed record Seeded(
        HttpClient Client, CapturingFixPublisher Publisher, long OrgId, long ProjectId, string IssueId);

    private async Task<Seeded> SeedAsync(string fingerprint)
    {
        var publisher = new CapturingFixPublisher();
        var client = ControlPlaneApp.Create(pg.ConnectionString)
            .WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IFixRequestPublisher>(publisher)))
            .CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs", new { name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { name = "Backend", platform = "python" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping(fingerprint, "TypeError: boom", "run"), Level.Error, DateTimeOffset.UtcNow);
        var issueId = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/issues"))
            .GetProperty("issues")[0].GetProperty("id").GetString()!;
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos", new { repoFullName = "acme/api" });
        return new Seeded(client, publisher, orgId, projectId, issueId);
    }

    private Task SetExecutionAsync(long orgId, FixExecution execution) =>
        new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 0, costCapUsd: null, fixExecution: (int)execution);

    private Task<int> UsedAsync(long orgId) =>
        new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow);

    private static Task<HttpResponseMessage> RequestFixAsync(Seeded s) =>
        s.Client.PostAsJsonAsync($"/api/projects/{s.ProjectId}/issues/{s.IssueId}/fix", new { });

    private static async Task<string> OnlyRunIdAsync(Seeded s) =>
        Assert.Single((await s.Client.GetFromJsonAsync<JsonElement[]>(
            $"/api/projects/{s.ProjectId}/issues/{s.IssueId}/fixes"))!).GetProperty("id").GetString()!;

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, long projectId, string fixId) =>
        client.PostAsync($"/api/projects/{projectId}/fixes/{fixId}/cancel", content: null);

    [Fact]
    public async Task A_runner_run_nobody_took_stops_blocking_its_issue_once_cancelled()
    {
        // The check the defect was filed with: queue for a runner, leave runner mode, ask again.
        var s = await SeedAsync("fp-cancel-runner");
        await SetExecutionAsync(s.OrgId, FixExecution.Runner);
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);
        await SetExecutionAsync(s.OrgId, FixExecution.Hosted);
        Assert.Equal(HttpStatusCode.Conflict, (await RequestFixAsync(s)).StatusCode);
        Assert.Equal(1, await UsedAsync(s.OrgId));

        var cancel = await CancelAsync(s.Client, s.ProjectId, await OnlyRunIdAsync(s));

        Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        Assert.Equal(0, await UsedAsync(s.OrgId)); // its reservation went back with it
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);
        Assert.NotNull(s.Publisher.Job);
    }

    [Fact]
    public async Task A_cancelled_runner_run_is_never_leased()
    {
        var s = await SeedAsync("fp-cancel-lease");
        await SetExecutionAsync(s.OrgId, FixExecution.Runner);
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent, (await CancelAsync(s.Client, s.ProjectId, await OnlyRunIdAsync(s))).StatusCode);

        Assert.Null(await new PostgresJobLeaseStore(pg.ConnectionString).TryClaimAsync(s.OrgId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_run_a_runner_holds_is_not_cancelled_and_keeps_its_reservation()
    {
        // Something is executing it, and cancelling the row could not stop the draft PR it may open.
        var s = await SeedAsync("fp-cancel-leased");
        await SetExecutionAsync(s.OrgId, FixExecution.Runner);
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);
        Assert.NotNull(
            await new PostgresJobLeaseStore(pg.ConnectionString).TryClaimAsync(s.OrgId, DateTimeOffset.UtcNow));

        var cancel = await CancelAsync(s.Client, s.ProjectId, await OnlyRunIdAsync(s));

        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal("fix_not_cancellable",
            (await cancel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(1, await UsedAsync(s.OrgId));
    }

    [Fact]
    public async Task A_run_whose_runner_lost_its_lease_is_cancelled_and_its_late_report_refused()
    {
        // A runner that died holding the lease left the run running, and nothing else ends a runner's run.
        var s = await SeedAsync("fp-cancel-lapsed");
        await SetExecutionAsync(s.OrgId, FixExecution.Runner);
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);
        var leases = new PostgresJobLeaseStore(pg.ConnectionString);
        var job = await leases.TryClaimAsync(s.OrgId, DateTimeOffset.UtcNow.AddDays(-1)); // lapsed long ago
        Assert.NotNull(job);

        Assert.Equal(
            HttpStatusCode.NoContent, (await CancelAsync(s.Client, s.ProjectId, await OnlyRunIdAsync(s))).StatusCode);

        Assert.Equal(0, await UsedAsync(s.OrgId));
        Assert.Null(await leases.TryClaimAsync(s.OrgId, DateTimeOffset.UtcNow));
        // Reported at the instant it was leased, so only the cleared lease, not the clock, can refuse it.
        Assert.Null(await leases.TryReportAsync(
            job!.FixId, job.LeaseId, FixStatus.Failed, "", "", "", "", 0, 0, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Equal(0, await UsedAsync(s.OrgId)); // a late failure report refunds nothing more
    }

    [Fact]
    public async Task A_hosted_run_the_conductor_has_started_is_not_cancelled()
    {
        var s = await SeedAsync("fp-cancel-hosted-running");
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);
        Assert.NotNull(await new PostgresFixStore(pg.ConnectionString)
            .TryClaimAsync(s.Publisher.Job!.ToPendingRun("fake", DateTimeOffset.UtcNow)));

        Assert.Equal(
            HttpStatusCode.Conflict, (await CancelAsync(s.Client, s.ProjectId, await OnlyRunIdAsync(s))).StatusCode);
        Assert.Equal(1, await UsedAsync(s.OrgId));
    }

    [Fact]
    public async Task A_cancelled_hosted_run_whose_job_arrives_later_is_neither_run_nor_refunded_again()
    {
        var s = await SeedAsync("fp-cancel-hosted");
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);
        var fixId = await OnlyRunIdAsync(s);
        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(s.Client, s.ProjectId, fixId)).StatusCode);

        var outcome = await new FixOrchestrator(new PostgresFixStore(pg.ConnectionString), new FakeFixProvider())
            .RunAsync(s.Publisher.Job!);

        Assert.Null(outcome);
        Assert.Equal(0, await UsedAsync(s.OrgId));
        var detail = await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/fixes/{fixId}");
        Assert.Equal((int)FixStatus.Cancelled, detail.GetProperty("status").GetInt32());
        Assert.Contains(
            detail.GetProperty("audit").EnumerateArray(), e => e.GetProperty("event").GetString() == "cancelled");
    }

    [Fact]
    public async Task Cancelling_twice_refunds_once()
    {
        var s = await SeedAsync("fp-cancel-twice");
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted,
            (await s.Client.PostAsJsonAsync(
                $"/api/projects/{s.ProjectId}/issues/{(await SeedOtherIssueAsync(s))}/fix", new { })).StatusCode);
        var fixId = await OnlyRunIdAsync(s);

        Assert.Equal(HttpStatusCode.NoContent, (await CancelAsync(s.Client, s.ProjectId, fixId)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await CancelAsync(s.Client, s.ProjectId, fixId)).StatusCode);

        Assert.Equal(1, await UsedAsync(s.OrgId)); // the other issue's run still holds its own
    }

    private async Task<string> SeedOtherIssueAsync(Seeded s)
    {
        await new IssueRepository(pg.ConnectionString).UpsertAsync(
            s.ProjectId, new Grouping("fp-other-" + Guid.NewGuid().ToString("N"), "KeyError: x", "run"),
            Level.Error, DateTimeOffset.UtcNow);
        var issues = (await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/issues"))
            .GetProperty("issues").EnumerateArray();
        return issues.Select(i => i.GetProperty("id").GetString()!).First(id => id != s.IssueId);
    }

    [Fact]
    public async Task Another_projects_run_reads_as_not_found()
    {
        var s = await SeedAsync("fp-cancel-tenant");
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);
        var other = await SeedAsync("fp-cancel-tenant-other");

        // The other org's admin names its own project with this run's id.
        var cancel = await CancelAsync(other.Client, other.ProjectId, await OnlyRunIdAsync(s));

        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal(1, await UsedAsync(s.OrgId));
    }

    [Fact]
    public async Task A_member_cannot_cancel()
    {
        var s = await SeedAsync("fp-cancel-member");
        Assert.Equal(HttpStatusCode.Accepted, (await RequestFixAsync(s)).StatusCode);
        var member = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        var memberUser = await ApiAuth.SignUpAsync(member, $"member-{Guid.NewGuid():N}@condux.test");
        var invite = await s.Client.PostAsJsonAsync($"/api/orgs/{s.OrgId}/invites",
            new { email = memberUser.Email, role = "member" });
        var token = (await invite.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        (await member.PostAsJsonAsync("/api/invites/accept", new { token })).EnsureSuccessStatusCode();

        Assert.Equal(
            HttpStatusCode.Forbidden, (await CancelAsync(member, s.ProjectId, await OnlyRunIdAsync(s))).StatusCode);
        Assert.Equal(1, await UsedAsync(s.OrgId));
    }

    // A runner bump queued the way the trigger queues one: reserved first, then the leasable row.
    private async Task<(Guid RepoId, Guid RunId)> QueueRunnerBumpAsync(Seeded s, string ghsa)
    {
        var repoId = Guid.Parse((await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/repos"))
            [0].GetProperty("id").GetString()!);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await new PostgresAiFixQuota(pg.ConnectionString).TryConsumeAsync(s.OrgId, 0, now));
        var run = new CveFixRun(
            Guid.CreateVersion7(), repoId, ghsa, null, "lodash", "npm", "< 4.17.21", "4.17.21",
            "https://example.invalid/advisory", FixStatus.Pending, PostgresJobLeaseStore.RunnerProvider,
            Model: "", Branch: "", PrUrl: "", Summary: "", Actor: "1", now, now)
        { ReservedAt = now };
        Assert.True(await new PostgresJobLeaseStore(pg.ConnectionString).EnqueueCveAsync(
            run, new RunnerJobContext("main", "Bump lodash.", ["package.json"])));
        return (repoId, run.Id);
    }

    private static Task<HttpResponseMessage> CancelBumpAsync(Seeded s, Guid repoId, Guid runId) =>
        s.Client.PostAsync($"/api/projects/{s.ProjectId}/repos/{repoId}/cve-fixes/{runId}/cancel", content: null);

    [Fact]
    public async Task A_pending_bump_is_cancelled_and_frees_its_advisory()
    {
        var s = await SeedAsync("fp-cancel-bump");
        var (repoId, runId) = await QueueRunnerBumpAsync(s, "GHSA-aaaa-bbbb-cccc");

        Assert.Equal(HttpStatusCode.NoContent, (await CancelBumpAsync(s, repoId, runId)).StatusCode);

        Assert.Equal(0, await UsedAsync(s.OrgId));
        Assert.False(
            await new PostgresCveFixStore(pg.ConnectionString).HasActiveRunAsync(repoId, "GHSA-aaaa-bbbb-cccc"));
        Assert.Null(await new PostgresJobLeaseStore(pg.ConnectionString).TryClaimAsync(s.OrgId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_bump_a_runner_holds_is_not_cancelled()
    {
        var s = await SeedAsync("fp-cancel-bump-leased");
        var (repoId, runId) = await QueueRunnerBumpAsync(s, "GHSA-dddd-eeee-ffff");
        Assert.NotNull(
            await new PostgresJobLeaseStore(pg.ConnectionString).TryClaimAsync(s.OrgId, DateTimeOffset.UtcNow));

        Assert.Equal(HttpStatusCode.Conflict, (await CancelBumpAsync(s, repoId, runId)).StatusCode);
        Assert.Equal(1, await UsedAsync(s.OrgId));
    }

    [Fact]
    public async Task A_bump_whose_runner_lost_its_lease_is_cancelled()
    {
        var s = await SeedAsync("fp-cancel-bump-lapsed");
        var (repoId, runId) = await QueueRunnerBumpAsync(s, "GHSA-kkkk-mmmm-nnnn");
        Assert.NotNull(await new PostgresJobLeaseStore(pg.ConnectionString)
            .TryClaimAsync(s.OrgId, DateTimeOffset.UtcNow.AddDays(-1)));

        Assert.Equal(HttpStatusCode.NoContent, (await CancelBumpAsync(s, repoId, runId)).StatusCode);
        Assert.Equal(0, await UsedAsync(s.OrgId));
    }

    [Fact]
    public async Task A_hosted_bump_the_conductor_has_started_is_not_cancelled()
    {
        var s = await SeedAsync("fp-cancel-bump-hosted");
        var repoId = Guid.Parse((await s.Client.GetFromJsonAsync<JsonElement>($"/api/projects/{s.ProjectId}/repos"))
            [0].GetProperty("id").GetString()!);
        var now = DateTimeOffset.UtcNow;
        Assert.True(await new PostgresAiFixQuota(pg.ConnectionString).TryConsumeAsync(s.OrgId, 0, now));
        var run = new CveFixRun(
            Guid.CreateVersion7(), repoId, "GHSA-pppp-qqqq-rrrr", null, "lodash", "npm", "< 4.17.21", "4.17.21",
            "https://example.invalid/advisory", FixStatus.Pending, "", Model: "", Branch: "", PrUrl: "",
            Summary: "", Actor: "1", now, now)
        { JobId = Guid.CreateVersion7(), ReservedAt = now };
        var store = new PostgresCveFixStore(pg.ConnectionString);
        Assert.True(await store.TryQueueAsync(run));
        Assert.NotNull(await store.TryClaimAsync(run with { Provider = "fake" }));

        Assert.Equal(HttpStatusCode.Conflict, (await CancelBumpAsync(s, repoId, run.Id)).StatusCode);
        Assert.Equal(1, await UsedAsync(s.OrgId));
    }

    [Fact]
    public async Task Another_projects_bump_reads_as_not_found()
    {
        var s = await SeedAsync("fp-cancel-bump-tenant");
        var (repoId, runId) = await QueueRunnerBumpAsync(s, "GHSA-ssss-tttt-uuuu");
        var other = await SeedAsync("fp-cancel-bump-tenant-other");

        // The other org's admin names its own project with this repository and run.
        var cancel = await other.Client.PostAsync(
            $"/api/projects/{other.ProjectId}/repos/{repoId}/cve-fixes/{runId}/cancel", content: null);

        Assert.Equal(HttpStatusCode.NotFound, cancel.StatusCode);
        Assert.Equal(1, await UsedAsync(s.OrgId));
    }

    [Fact]
    public async Task A_bump_named_under_another_repository_reads_as_not_found()
    {
        var s = await SeedAsync("fp-cancel-bump-repo");
        var (_, runId) = await QueueRunnerBumpAsync(s, "GHSA-gggg-hhhh-jjjj");
        var other = await s.Client.PostAsJsonAsync(
            $"/api/projects/{s.ProjectId}/repos", new { repoFullName = "acme/web" });
        var otherRepoId = Guid.Parse(
            (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!);

        Assert.Equal(HttpStatusCode.NotFound, (await CancelBumpAsync(s, otherRepoId, runId)).StatusCode);
        Assert.Equal(1, await UsedAsync(s.OrgId));
    }
}
