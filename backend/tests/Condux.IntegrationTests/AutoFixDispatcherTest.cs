extern alias consumer;

using System.Text;
using Condux.Core.Alerting;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.Core.OrgNotifications;
using Condux.Core.Plans;
using Condux.Core.Quotas;
using Condux.IntegrationTests.Fixtures;
using Condux.Notifications;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Condux.Storage.SourceMaps;
using Xunit;
using AutoFixDispatcher = consumer::Condux.Consumer.AutoFixDispatcher;

namespace Condux.IntegrationTests;

/// <summary>
/// Auto-fix mode dispatch (#101): with an org in auto mode on a paid plan, a repo linked, and a new
/// error-level issue, the consumer publishes a <see cref="FixJob"/> for it (carrying the org, issue,
/// repo, and installation) — reserving from the same allowance as the manual flow. Manual mode and
/// sub-error issues publish nothing. The pure trigger rule is unit-tested in <c>AutoFixPolicyTests</c>;
/// this covers the orchestration against real Postgres with a capturing publisher.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AutoFixDispatcherTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // Captures the pause notices the dispatcher fans out via the org's notification channels, so a test can
    // assert auto-fix actually told the org why it paused (not just that it published no fix job).
    private sealed class CapturingNotifier : INotifier
    {
        public List<(string Subject, string Body)> Sent { get; } = [];
        public NotificationChannel Channel => NotificationChannel.Webhook;

        public Task SendAsync(
            AlertChannel target, AlertNotification notification, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SendMessageAsync(
            string target, string subject, string body, CancellationToken cancellationToken = default)
        {
            Sent.Add((subject, body));
            return Task.CompletedTask;
        }
    }

    // A sink (an org notification channel's notifier) makes the pause dispatch observable; null keeps the
    // old behavior (no channels, so the notice is a no-op that still exercises the throttle).
    private ConductorPauseNotifier PauseNotifier(INotifier? sink) => new(
        new PostgresPauseNotifyThrottle(pg.ConnectionString),
        new OrgNotificationDispatcher(
            new OrgNotificationChannelRepository(pg.ConnectionString), sink is null ? [] : [sink],
            NullLogger<OrgNotificationDispatcher>.Instance),
        NullLogger<ConductorPauseNotifier>.Instance);

    private AutoFixDispatcher Dispatcher(
        IFixRequestPublisher publisher, INotifier? pauseSink = null, FrameSymbolicator? symbolicator = null) => new(
        new ProjectRepository(pg.ConnectionString), new OrgRepository(pg.ConnectionString),
        new RepoLinkRepository(pg.ConnectionString), new ReleaseRepository(pg.ConnectionString),
        new GithubInstallationRepository(pg.ConnectionString),
        new PostgresAiFixQuota(pg.ConnectionString), new PostgresAiFixSpend(pg.ConnectionString), publisher,
        new PostgresJobLeaseStore(pg.ConnectionString), new PostgresFixStore(pg.ConnectionString),
        PauseNotifier(pauseSink),
        new EventSymbolication(symbolicator, native: null, NullLogger<EventSymbolication>.Instance),
        NullLogger<AutoFixDispatcher>.Instance);

    // An org at the given tier + mode, a project, and optionally a linked repo + GitHub installation.
    //
    // The installation id is derived from the org rather than being a constant, because every call makes a
    // NEW org and the class shares one database. A shared id used to work only because linking overwrote
    // the owning org, so six of the seven callers here were quietly seeding an installation that belonged
    // to a different org and passing anyway. Linking now refuses that, which is why the seed asserts.
    private async Task<(long OrgId, long ProjectId, long InstallationId)> SeedAsync(
        int tier, int mode, bool withRepo, bool withInstall)
    {
        var orgs = new OrgRepository(pg.ConnectionString);
        var org = await orgs.CreateAsync("org-" + Guid.NewGuid().ToString("N"), "Org", tier);
        if (mode != 0)
        {
            await orgs.UpdateSettingsAsync(
                org.Id, mode, costCapUsd: null, fixExecution: (int)FixExecution.Hosted);
        }
        var project = await new ProjectRepository(pg.ConnectionString).CreateAsync(org.Id, "Backend", "python");
        if (withRepo)
        {
            await new RepoLinkRepository(pg.ConnectionString).LinkAsync(project.Id, "acme/api", "main");
        }
        var installationId = 500_000 + org.Id;
        if (withInstall)
        {
            Assert.True(
                await new GithubInstallationRepository(pg.ConnectionString).LinkAsync(installationId, org.Id),
                $"installation {installationId} is already held by another org");
        }
        return (org.Id, project.Id, installationId);
    }

    private async Task<UpsertResult> NewIssueAsync(long projectId, string fingerprint, Level level) =>
        await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping(fingerprint, "Boom", "run"), level, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Auto_org_publishes_a_fix_job_for_a_new_error_issue()
    {
        var (orgId, projectId, installationId) = await SeedAsync(
            (int)Tier.Team, mode: 1, withRepo: true, withInstall: true);
        var upsert = await NewIssueAsync(projectId, "fp-auto", Level.Error);
        var pub = new CapturingFixPublisher();

        await Dispatcher(pub).DispatchAsync(projectId, upsert, new Event { EventId = "e1", Level = Level.Error });

        var job = Assert.Single(pub.Jobs);
        Assert.Equal(upsert.Id, job.IssueId);
        Assert.Equal(orgId, job.OrgId);
        Assert.Equal("acme/api", job.RepoFullName);
        Assert.Equal("main", job.BaseBranch);
        Assert.Equal(installationId, job.InstallationId);
        Assert.Equal("auto", job.Actor);
    }

    [Fact]
    public async Task Manual_mode_and_sub_error_issues_publish_nothing()
    {
        var pub = new CapturingFixPublisher();

        // Manual mode: even a new error issue is left for a human to trigger.
        var (_, manualProject, _) = await SeedAsync((int)Tier.Team, mode: 0, withRepo: true, withInstall: true);
        await Dispatcher(pub).DispatchAsync(manualProject,
            await NewIssueAsync(manualProject, "fp-manual", Level.Error), new Event { Level = Level.Error });
        Assert.Empty(pub.Jobs);

        // Auto mode but a warning (below error) — auto-fix skips low-value issues.
        var (_, autoProject, _) = await SeedAsync((int)Tier.Team, mode: 1, withRepo: true, withInstall: true);
        await Dispatcher(pub).DispatchAsync(autoProject,
            await NewIssueAsync(autoProject, "fp-warn", Level.Warning), new Event { Level = Level.Warning });
        Assert.Empty(pub.Jobs);

        // Auto mode + error but no repo linked — nowhere to open a PR.
        var (_, noRepoProject, _) = await SeedAsync((int)Tier.Team, mode: 1, withRepo: false, withInstall: true);
        await Dispatcher(pub).DispatchAsync(noRepoProject,
            await NewIssueAsync(noRepoProject, "fp-norepo", Level.Error), new Event { Level = Level.Error });
        Assert.Empty(pub.Jobs);
    }

    [Fact]
    public async Task Auto_org_over_its_cost_cap_publishes_nothing_and_notifies()
    {
        var (orgId, projectId, installationId) = await SeedAsync(
            (int)Tier.Team, mode: 1, withRepo: true, withInstall: true);
        await new OrgNotificationChannelRepository(pg.ConnectionString)
            .AddAsync(orgId, NotificationChannel.Webhook, "https://hooks.test/pause");

        // A completed priced run already this month: 1M in @ $5 + 1M out @ $25 = $30 spend.
        var priced = await NewIssueAsync(projectId, "fp-spent", Level.Error);
        var now = DateTimeOffset.UtcNow;
        var run = new FixSuggestion(
            Guid.CreateVersion7(), priced.Id, "acme/api", FixStatus.Succeeded, "anthropic", "claude-opus-4-8",
            "b", "https://example.invalid/p/1", "s", now, now)
        { InputTokens = 1_000_000, OutputTokens = 1_000_000 };
        var fixes = new PostgresFixStore(pg.ConnectionString);
        await fixes.InsertAsync(run);
        await fixes.UpdateAsync(run);

        // Set a $10 cap — already blown by the $30 spent — then a new error issue lands.
        await new OrgRepository(pg.ConnectionString).UpdateSettingsAsync(
            orgId, mode: 1, costCapUsd: 10m, fixExecution: (int)FixExecution.Hosted);
        var pub = new CapturingFixPublisher();
        var sink = new CapturingNotifier();
        await Dispatcher(pub, sink).DispatchAsync(projectId,
            await NewIssueAsync(projectId, "fp-capped", Level.Error), new Event { Level = Level.Error });

        Assert.Empty(pub.Jobs); // over the cap → auto-fix skips
        var notice = Assert.Single(sink.Sent); // and the org is told why it paused
        Assert.Equal(ConductorPauseText.Subject(ConductorPauseReason.CostCapReached), notice.Subject);
    }

    [Fact]
    public async Task Auto_org_out_of_allowance_publishes_nothing_and_notifies()
    {
        var (orgId, projectId, installationId) = await SeedAsync(
            (int)Tier.Team, mode: 1, withRepo: true, withInstall: true);
        await new OrgNotificationChannelRepository(pg.ConnectionString)
            .AddAsync(orgId, NotificationChannel.Webhook, "https://hooks.test/pause");

        // Exhaust the month's included allowance so the dispatcher's reservation fails. No spend is recorded,
        // so the cost cap is clear and execution reaches the allowance check. Consume exactly the tier's
        // allowance from the catalog (not a hardcoded count), so this can't rot if the number changes.
        var quota = new PostgresAiFixQuota(pg.ConnectionString);
        var allowance = PlanCatalog.For(Tier.Team).AiFixesPerMonth;
        for (var i = 0; i < allowance; i++)
        {
            Assert.True(await quota.TryConsumeAsync(orgId, allowance, DateTimeOffset.UtcNow));
        }

        var pub = new CapturingFixPublisher();
        var sink = new CapturingNotifier();
        await Dispatcher(pub, sink).DispatchAsync(projectId,
            await NewIssueAsync(projectId, "fp-noallowance", Level.Error), new Event { Level = Level.Error });

        Assert.Empty(pub.Jobs); // out of allowance → auto-fix skips
        var notice = Assert.Single(sink.Sent);
        Assert.Equal(ConductorPauseText.Subject(ConductorPauseReason.AllowanceExhausted), notice.Subject);
    }

    [Fact]
    public async Task A_bundled_frame_is_scoped_to_the_source_file_its_map_names()
    {
        // A Next.js app reports a minified chunk that no repository holds. Its source map names the file
        // the way Turbopack writes it, relative to the workspace root, so the fix scopes to that repository
        // path with no code mapping at all. Without the symbolication the job names the chunk and the run
        // reads nothing.
        var (projectId, bundled) = await SeedBundledAsync();
        var pub = new CapturingFixPublisher();

        await Dispatcher(pub, symbolicator: new FrameSymbolicator(
                new SourceMapArtifactRepository(pg.ConnectionString),
                new InMemoryMapStore(Encoding.UTF8.GetBytes(BundledMap), BundledMapKey)))
            .DispatchAsync(projectId, await NewIssueAsync(projectId, "fp-bundled", Level.Error), bundled);

        Assert.Equal(["web/src/cart.ts"], Assert.Single(pub.Jobs).ScopedPaths);
    }

    [Fact]
    public async Task A_map_store_that_fails_still_requests_the_fix_on_the_raw_frames()
    {
        var (projectId, bundled) = await SeedBundledAsync();
        var pub = new CapturingFixPublisher();

        await Dispatcher(pub, symbolicator: new FrameSymbolicator(
                new SourceMapArtifactRepository(pg.ConnectionString), new UnreachableMapStore()))
            .DispatchAsync(projectId, await NewIssueAsync(projectId, "fp-bundled-down", Level.Error), bundled);

        Assert.Equal(["chunk.js"], Assert.Single(pub.Jobs).ScopedPaths);
    }

    [Fact]
    public async Task A_dispatch_cancelled_while_reading_maps_hands_the_run_back()
    {
        // Nothing has been sent when a read is cancelled, so the run reserved for it is refunded.
        var (projectId, bundled) = await SeedBundledAsync();
        var orgId = (await new ProjectRepository(pg.ConnectionString).GetAsync(projectId))!.OrgId;
        var store = new HangingMapStore();
        var pub = new CapturingFixPublisher();
        using var shutdown = new CancellationTokenSource();

        var dispatch = Dispatcher(pub, symbolicator: new FrameSymbolicator(
                new SourceMapArtifactRepository(pg.ConnectionString), store))
            .DispatchAsync(
                projectId, await NewIssueAsync(projectId, "fp-bundled-slow", Level.Error), bundled, shutdown.Token);
        await store.Reached.Task.WaitAsync(Bound);
        await shutdown.CancelAsync();
        await dispatch.WaitAsync(Bound);

        Assert.Empty(pub.Jobs);
        Assert.Equal(0, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task An_org_with_no_runs_left_reads_no_maps()
    {
        // The allowance is checked before the context is built, so an exhausted org costs the drain two
        // queries and not a source-map fetch per new issue.
        var (projectId, bundled) = await SeedBundledAsync();
        var orgId = (await new ProjectRepository(pg.ConnectionString).GetAsync(projectId))!.OrgId;
        var quota = new PostgresAiFixQuota(pg.ConnectionString);
        var limit = PlanCatalog.For(Tier.Team).AiFixesPerMonth;
        while (await quota.TryConsumeAsync(orgId, limit, DateTimeOffset.UtcNow))
        {
        }
        var store = new HangingMapStore();

        await Dispatcher(new CapturingFixPublisher(), symbolicator: new FrameSymbolicator(
                new SourceMapArtifactRepository(pg.ConnectionString), store))
            .DispatchAsync(projectId, await NewIssueAsync(projectId, "fp-bundled-spent", Level.Error), bundled)
            .WaitAsync(Bound);

        Assert.False(store.Reached.Task.IsCompleted);
    }

    [Fact]
    public async Task A_publish_cancelled_in_flight_withdraws_the_run_and_refunds_it()
    {
        // A cancelled produce stops the wait, not the send, so the job may still arrive. Its run is cancelled
        // first, so if it does it finds the run no longer pending and does not run: the refund is safe.
        var (orgId, projectId, _) = await SeedAsync((int)Tier.Team, mode: 1, withRepo: true, withInstall: true);
        var publisher = new HangingFixPublisher();
        using var shutdown = new CancellationTokenSource();

        var issue = await NewIssueAsync(projectId, "fp-publish-slow", Level.Error);
        var dispatch = Dispatcher(publisher).DispatchAsync(
            projectId, issue, new Event { Level = Level.Error }, shutdown.Token);
        await publisher.Reached.Task.WaitAsync(Bound);
        await shutdown.CancelAsync();
        await dispatch.WaitAsync(Bound);

        Assert.Equal(0, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        Assert.Equal(FixStatus.Cancelled, Assert.Single(
            await new PostgresFixStore(pg.ConnectionString).ListByIssueAsync(issue.Id)).Status);
    }

    [Fact]
    public async Task An_issue_with_a_run_in_progress_gets_no_second_one()
    {
        // As when an issue regresses again before the first fix concludes. The first request's pending run
        // refuses the second, which hands its reservation back rather than queueing a second draft PR.
        var (orgId, projectId, _) = await SeedAsync((int)Tier.Team, mode: 1, withRepo: true, withInstall: true);
        var upsert = await NewIssueAsync(projectId, "fp-auto-twice", Level.Error);
        var pub = new CapturingFixPublisher();

        await Dispatcher(pub).DispatchAsync(projectId, upsert, new Event { Level = Level.Error });
        await Dispatcher(pub).DispatchAsync(projectId, upsert, new Event { Level = Level.Error });

        Assert.Single(pub.Jobs);
        Assert.Equal(1, await new PostgresAiFixQuota(pg.ConnectionString).GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        var run = Assert.Single(await new PostgresFixStore(pg.ConnectionString).ListByIssueAsync(upsert.Id));
        Assert.Equal(FixStatus.Pending, run.Status);
    }

    // Long enough for any real wait here, short enough that a regression fails the run instead of hanging it.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // A broker that never acknowledges until the caller gives up.
    private sealed class HangingFixPublisher : IFixRequestPublisher
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PublishAsync(FixJob job, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private const string BundledMapKey = "sourcemaps/by-debug-id/cart";

    private const string BundledMap =
        """{"version":3,"sources":["turbopack:///[project]/web/src/cart.ts"],"sourcesContent":["const a = 1;\nfunction boom() {\n  throw new Error();\n}\n"],"names":["boom"],"mappings":"AAAA,CAEA;KAAGA"}""";

    // An auto-mode project with a map recorded for one chunk, and an event whose only frame is in it.
    private async Task<(long ProjectId, Event Bundled)> SeedBundledAsync()
    {
        var (_, projectId, _) = await SeedAsync((int)Tier.Team, mode: 1, withRepo: true, withInstall: true);
        await new SourceMapArtifactRepository(pg.ConnectionString).RecordAsync(
            projectId, "web@1", dist: null, debugId: "cart", filename: "chunk.js",
            objectKey: BundledMapKey, byteSize: BundledMap.Length);
        return (projectId, new Event
        {
            Level = Level.Error,
            Release = "web@1",
            DebugImages =
                [new DebugImage { Type = "sourcemap", CodeFile = "https://app/_next/chunk.js", DebugId = "cart" }],
            Exceptions =
            [
                new ExceptionValue
                {
                    Type = "TypeError",
                    Stacktrace = new Stacktrace
                    {
                        Frames =
                        [
                            new Frame
                            {
                                InApp = true, Filename = "chunk.js", AbsPath = "https://app/_next/chunk.js",
                                Function = "n", Lineno = 2, Colno = 6,
                            },
                        ],
                    },
                },
            ],
        });
    }

    [Fact]
    public async Task Fix_job_prompt_carries_the_release_attribution()
    {
        var (_, projectId, _) = await SeedAsync((int)Tier.Team, mode: 1, withRepo: true, withInstall: true);
        // Record the release the event will carry → its commit, so the fix prompt gets the bisect hint (#144).
        var repo = (await new RepoLinkRepository(pg.ConnectionString).ListByProjectAsync(projectId))[0];
        await new ReleaseRepository(pg.ConnectionString).RecordAsync(projectId, repo.Id, "1.4.2", "abc123def456");
        var upsert = await NewIssueAsync(projectId, "fp-release", Level.Error);
        var pub = new CapturingFixPublisher();

        await Dispatcher(pub).DispatchAsync(
            projectId, upsert, new Event { Level = Level.Error, Release = "1.4.2" });

        var job = Assert.Single(pub.Jobs);
        Assert.Contains("first appeared in release 1.4.2, built from commit abc123def456", job.Prompt);
    }
}
