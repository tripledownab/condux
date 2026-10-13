using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Condux.GitHub;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// What a failed CVE-bump enqueue does to the run it reserved. Its pending run is cancelled and the
/// reservation refunded, a wait the client cancelled included: a cancelled produce stops the wait and not
/// the send, but a job that arrives after all meets its run cancelled and does not run. GitHub is stubbed
/// at the HTTP boundary; the route, the quota and Postgres are real.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CveFixEnqueueTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string GhsaId = "GHSA-jf85-cpcp-j695";

    // Keeps the job, so a test can deliver it after the publish was reported failed.
    private sealed class ThrowingPublisher(Exception failure) : ICveFixPublisher
    {
        public CveFixJob? Job { get; private set; }

        public Task PublishAsync(CveFixJob job, CancellationToken cancellationToken = default)
        {
            Job = job;
            throw failure;
        }
    }

    // Answers the installation token mint and the Dependabot alert list, the two calls the trigger makes.
    // listRanges false answers an advisory with no range list, which GitHub's schema requires but a
    // reader must not take as "this alert's range is the only one".
    private sealed class StubGitHub(bool listRanges = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.RequestUri!.AbsolutePath.EndsWith("/access_tokens", StringComparison.Ordinal)
                ? $$"""{"token":"ghs_stub","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}"""
                : $$$"""
                  [{"html_url":"https://example.test/alert",
                    "security_advisory":{"ghsa_id":"{{{GhsaId}}}","cve_id":"CVE-2019-10744",
                      "severity":"critical","summary":"Prototype pollution",
                      "{{{(listRanges ? "vulnerabilities" : "not_vulnerabilities")}}}":[
                        {"package":{"ecosystem":"npm","name":"lodash"},"vulnerable_version_range":"< 4.17.12"},
                        {"package":{"ecosystem":"npm","name":"lodash"},"vulnerable_version_range":">= 5.0.0, < 5.0.3"},
                        {"package":{"ecosystem":"npm","name":"Lodash"},"vulnerable_version_range":"< 9.0.0"},
                        {"package":{"ecosystem":"npm","name":"lodash-es"},"vulnerable_version_range":"< 4.17.12"}]},
                    "security_vulnerability":{"package":{"ecosystem":"npm","name":"lodash"},
                      "vulnerable_version_range":"< 4.17.12","first_patched_version":{"identifier":"4.17.12"}},
                    "dependency":{"manifest_path":"package.json"}}]
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    // A publish that fails with a broker error once the request is cancelled, so a client that hung up
    // reaches the refund with its token already fired.
    private sealed class FailsAfterHangUpPublisher : ICveFixPublisher
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(CveFixJob job, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            throw new InvalidOperationException("broker unreachable");
        }
    }

    // A publish still waiting when the client hangs up, which the endpoint sees as a cancellation.
    private sealed class HangingPublisher : ICveFixPublisher
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(CveFixJob job, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private async Task<(HttpClient Client, long OrgId, long ProjectId, string RepoId)> ProvisionAsync(
        ICveFixPublisher publisher, Action<IServiceCollection>? extra = null, bool listRanges = true)
    {
        var github = new HttpClient(new StubGitHub(listRanges));
        var options = new GitHubAppOptions("Iv1.client", RSA.Create(2048).ExportRSAPrivateKeyPem(), "whsec")
        {
            ApiBaseUrl = "https://gh.test",
        };
        var client = ControlPlaneApp.Create(pg.ConnectionString, configure: b =>
            b.ConfigureServices(services =>
            {
                services.AddSingleton(publisher);
                services.AddSingleton(new GitHubInstallationTokens(github, options, () => DateTimeOffset.UtcNow));
                services.AddSingleton(new GitHubRepoClient(github, "https://gh.test"));
                extra?.Invoke(services);
            })).CreateClient();

        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs", new { name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);

        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { name = "Backend", platform = "javascript" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();

        await new GithubInstallationRepository(pg.ConnectionString)
            .LinkAsync(Random.Shared.NextInt64(1, long.MaxValue), orgId, "acme");
        var linkResp = await client.PostAsJsonAsync($"/api/projects/{projectId}/repos",
            new { repoFullName = "acme/api", defaultBranch = "main" });
        var repoId = (await linkResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        return (client, orgId, projectId, repoId);
    }

    [Fact]
    public async Task A_failed_publish_is_refunded()
    {
        var (client, orgId, projectId, repoId) =
            await ProvisionAsync(new ThrowingPublisher(new InvalidOperationException("broker unreachable")));

        var resp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal(0, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        // Its run is cancelled, which frees the advisory for another request.
        Assert.Equal(FixStatus.Cancelled, Assert.Single(
            await new PostgresCveFixStore(pg.ConnectionString).ListByRepoAsync(Guid.Parse(repoId))).Status);
    }

    [Fact]
    public async Task A_bump_that_arrives_after_its_publish_was_reported_failed_is_not_refunded_again()
    {
        // A delivery timeout cannot say whether the broker wrote the job, so the request refunded it. When it
        // arrives after all, behind a retry for the same advisory, it meets its own run cancelled: it is not
        // run, and not refunded as a refused job either, which would hand the run back twice.
        var publisher = new ThrowingPublisher(new InvalidOperationException("delivery timed out"));
        var (client, orgId, projectId, repoId) = await ProvisionAsync(publisher);
        await client.PostAsJsonAsync($"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId });
        var store = new PostgresCveFixStore(pg.ConnectionString);
        var lateJob = publisher.Job!;
        Assert.True(await store.TryQueueAsync(
            (lateJob with { JobId = Guid.CreateVersion7() }).ToPendingRun(provider: "", DateTimeOffset.UtcNow)));
        var quota = new PostgresAiFixQuota(pg.ConnectionString);
        await quota.TryConsumeAsync(orgId, 25, DateTimeOffset.UtcNow);

        Assert.Null(await new CveFixOrchestrator(store, new FakeFixProvider()).RunAsync(lateJob));

        Assert.Equal(1, await quota.GetUsedAsync(orgId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_failed_publish_is_refunded_after_the_client_hangs_up()
    {
        var publisher = new FailsAfterHangUpPublisher();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, orgId, projectId, repoId) = await ProvisionAsync(
            publisher, s => s.AddSingleton<IStartupFilter>(new SignalWhenHandled(handled, "/cve-fixes")));
        using var hangUp = new CancellationTokenSource();

        var request = client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId }, hangUp.Token);
        await publisher.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await hangUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_job_whose_wait_the_client_cancelled_is_withdrawn_and_refunded()
    {
        var publisher = new HangingPublisher();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (client, orgId, projectId, repoId) = await ProvisionAsync(
            publisher, s => s.AddSingleton<IStartupFilter>(new SignalWhenHandled(handled, "/cve-fixes")));
        using var hangUp = new CancellationTokenSource();

        var request = client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId }, hangUp.Token);
        await publisher.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await hangUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The job may still arrive, but it meets its run cancelled and does not run, so the refund is safe.
        Assert.Equal(0, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        Assert.Equal(FixStatus.Cancelled, Assert.Single(
            await new PostgresCveFixStore(pg.ConnectionString).ListByRepoAsync(Guid.Parse(repoId))).Status);
    }

    [Fact]
    public async Task A_hosted_bump_queued_but_not_yet_taken_refuses_a_second_request()
    {
        // The second request passes the in-progress check, as it would while the first is still being
        // queued. It meets the first one's pending run, written before the job was published, so it is
        // refused and refunded rather than published behind the first to open a second draft PR.
        var publisher = new CountingPublisher();
        var (client, orgId, projectId, repoId) = await ProvisionAsync(
            publisher,
            s => s.AddSingleton<ICveFixStore>(new SeesNoActiveRun(new PostgresCveFixStore(pg.ConnectionString))));
        var first = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("cve_fix_in_progress", (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
        Assert.Equal(1, publisher.Published);
        Assert.Equal(1, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        var run = Assert.Single(await new PostgresCveFixStore(pg.ConnectionString).ListByRepoAsync(Guid.Parse(repoId)));
        Assert.Equal(FixStatus.Pending, run.Status);
        Assert.Equal("", run.Provider);
    }

    private sealed class CountingPublisher : ICveFixPublisher
    {
        public int Published { get; private set; }

        public CveFixJob? Job { get; private set; }

        public Task PublishAsync(CveFixJob job, CancellationToken cancellationToken = default)
        {
            Published++;
            Job = job;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// An advisory with no range list stores none on the run, which verification reads as unverifiable,
    /// rather than this alert's range alone, which could call a version fixed that another range keeps.
    /// </summary>
    [Fact]
    public async Task A_bump_whose_advisory_lists_no_ranges_stores_none()
    {
        var publisher = new CountingPublisher();
        var (client, _, projectId, repoId) = await ProvisionAsync(publisher, listRanges: false);

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Empty(publisher.Job!.VulnerableRanges);
    }

    /// <summary>
    /// The request stores every range the advisory gives the bumped package (ADR-0051), its own first, on
    /// the job and on the run. npm names are exact, so "Lodash" and "lodash-es" are other packages.
    /// </summary>
    [Fact]
    public async Task A_bump_keeps_every_range_the_advisory_gives_its_package()
    {
        var publisher = new CountingPublisher();
        var (client, _, projectId, repoId) = await ProvisionAsync(publisher);

        var response = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        string[] expected = ["< 4.17.12", ">= 5.0.0, < 5.0.3"];
        Assert.Equal(expected, publisher.Job!.VulnerableRanges);
        var run = Assert.Single(await new PostgresCveFixStore(pg.ConnectionString).ListByRepoAsync(Guid.Parse(repoId)));
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT vulnerable_ranges FROM cve_fix_runs WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", run.Id);
        Assert.Equal(expected, (string[])(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_runner_bump_that_loses_the_race_for_its_advisory_is_refused_and_refunded()
    {
        // Two requests for one advisory both pass the in-progress check before either has queued. The
        // second meets the first one's row, so it is refused like the check would and its run comes back.
        var (client, orgId, projectId, repoId) = await ProvisionAsync(
            new ThrowingPublisher(new InvalidOperationException("a runner org never publishes")),
            s => s.AddSingleton<ICveFixStore>(new SeesNoActiveRun(new PostgresCveFixStore(pg.ConnectionString))));
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 0, costCapUsd: null, fixExecution: (int)FixExecution.Runner);
        var first = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/repos/{repoId}/cve-fixes", new { ghsaId = GhsaId });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("cve_fix_in_progress", (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
        Assert.Equal(1, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
    }

    // The real store, except that it never reports an advisory busy: the state of a request that checked
    // before the other one queued.
    private sealed class SeesNoActiveRun(PostgresCveFixStore inner) : ICveFixStore
    {
        public Task<bool> InsertAsync(CveFixRun run, CancellationToken ct = default) => inner.InsertAsync(run, ct);

        public Task<bool> TryQueueAsync(CveFixRun run, CancellationToken ct = default) => inner.TryQueueAsync(run, ct);

        public Task<bool> TryCancelQueuedAsync(CveFixRun run, CancellationToken ct = default) =>
            inner.TryCancelQueuedAsync(run, ct);

        public Task<Guid?> TryClaimAsync(CveFixRun run, CancellationToken ct = default) => inner.TryClaimAsync(run, ct);

        public Task<bool> MarkRunningAsync(CveFixRun run, CancellationToken ct = default) => inner.MarkRunningAsync(run, ct);

        public Task<bool> TryFailAsync(CveFixRun run, CancellationToken ct = default) => inner.TryFailAsync(run, ct);

        public Task RecordSuccessAsync(CveFixRun run, CancellationToken ct = default) =>
            inner.RecordSuccessAsync(run, ct);

        public Task<IReadOnlyList<CveFixRun>> ListByRepoAsync(Guid repoLinkId, CancellationToken ct = default) =>
            inner.ListByRepoAsync(repoLinkId, ct);

        public Task<bool> HasActiveRunAsync(Guid repoLinkId, string ghsaId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }
}
