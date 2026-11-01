using System.Globalization;
using Condux.Core.Events;
using Condux.Core.Auth;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Notifications;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// End-to-end coverage for the weekly digest aggregation (ADR-0031) against real Postgres + ClickHouse —
/// the SQL the unit tests stub over: the issue_received_1h volume split (this week vs last, top issues), the
/// new/regressed/resolved/open movement (incl. the resolved_at stamp + the regression reactivation), the
/// users-affected uniqExact over events, each counted by when Condux received the event, and the
/// org-scoped Conductor activity join. Plus the send ledger's once-per-week claim.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WeeklySummaryComposerTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    [Fact]
    public async Task Compose_aggregates_volume_movement_top_issues_users_and_fixes()
    {
        var (orgId, projectId) = await SeedOrgProjectAsync();
        var issues = new IssueRepository(pg.ConnectionString);

        // Seed timestamps off a base "now"; the evaluation instant (asOf) is captured *after* seeding so the
        // created_at / resolved_at / reopened_at the DB stamps during seeding fall inside the closing week
        // window. An issue that reached Condux before the week has its created_at placed there explicitly.
        var baseNow = DateTimeOffset.UtcNow;
        var thisWeek = FloorHour(baseNow.AddDays(-2));  // inside [asOf-7d, asOf)
        var prevWeek = FloorHour(baseNow.AddDays(-9));  // inside [asOf-14d, asOf-7d)
        var longAgo = baseNow.AddDays(-20);

        // NEW this week: first seen 2 days ago; 4 events this week.
        var newIssue = await issues.UpsertAsync(projectId, Group("fp-new", "New crash"), Level.Error, thisWeek);
        await WriteStatsAsync(projectId, newIssue.Id, Repeat(thisWeek, 4));

        // RECURRING: first seen long ago; 6 events this week + 5 last week — the top issue.
        var recurring = await issues.UpsertAsync(projectId, Group("fp-rec", "Recurring timeout"), Level.Error, longAgo);
        await PlaceCreatedAtAsync(recurring.Id, longAgo);
        // A repeat event on an open issue is not a reopen, so it must not count as a regression.
        await issues.UpsertAsync(projectId, Group("fp-rec", "Recurring timeout"), Level.Error, thisWeek);
        await WriteStatsAsync(projectId, recurring.Id, [.. Repeat(thisWeek, 6), .. Repeat(prevWeek, 5)]);

        // RESOLVED this week: old issue with 1 event this week, then resolved (stamps resolved_at).
        var resolved = await issues.UpsertAsync(projectId, Group("fp-res", "Resolved bug"), Level.Error, longAgo);
        await PlaceCreatedAtAsync(resolved.Id, longAgo);
        await WriteStatsAsync(projectId, resolved.Id, Repeat(thisWeek, 1));
        await issues.UpdateStatusAsync(projectId, resolved.PublicId, 2);

        // REGRESSED this week: old issue resolved then reopened. The reopening event claims an old time, as
        // a delayed or backdated one does; the reopen itself happened this week, so it still counts.
        var regressed = await issues.UpsertAsync(projectId, Group("fp-reg", "Regressed error"), Level.Error, longAgo);
        await PlaceCreatedAtAsync(regressed.Id, longAgo);
        await issues.UpdateStatusAsync(projectId, regressed.PublicId, 2);
        await issues.UpsertAsync(projectId, Group("fp-reg", "Regressed error"), Level.Error, longAgo.AddHours(1));
        await WriteStatsAsync(projectId, regressed.Id, Repeat(thisWeek, 2));

        // BUFFERED: an issue whose first event reaches Condux this week dated long ago. It is new this week,
        // whatever the event claims, and it was never reopened, so it is not a regression.
        await issues.UpsertAsync(projectId, Group("fp-buf", "Buffered crash"), Level.Error, longAgo);

        // BUFFERED AND REOPENED: also new this week, then resolved and reopened within it. It counts once, as
        // new, not a second time as a regression.
        var bufferedReopened = await issues.UpsertAsync(
            projectId, Group("fp-bufreo", "Buffered then reopened"), Level.Error, longAgo);
        await issues.UpdateStatusAsync(projectId, bufferedReopened.PublicId, 2);
        await issues.UpsertAsync(projectId, Group("fp-bufreo", "Buffered then reopened"), Level.Error, longAgo);

        // Two distinct users affected this week (uniqExact over events.user_key).
        await WriteEventsAsync(projectId, newIssue.Id, thisWeek, ["user-a", "user-b"]);

        // Conductor activity: one proposed (created this week, succeeded), one merged this week, one held.
        await SeedFixesAsync(recurring.Id, baseNow);

        var asOf = DateTimeOffset.UtcNow.AddMinutes(1);
        using var http = Http();
        var composer = new WeeklySummaryComposer(
            new ProjectRepository(pg.ConnectionString), issues,
            new ClickHouseIssueStatsReader(http), new ClickHouseEventReader(http),
            new PostgresFixStore(pg.ConnectionString));

        var summary = await composer.ComposeAsync(orgId, "Acme", asOf);

        Assert.Equal(13, summary.Events);        // 4 + 6 + 1 + 2
        Assert.Equal(5, summary.PreviousEvents);
        Assert.Equal(3, summary.NewIssues);      // new + both buffered
        Assert.Equal(1, summary.Regressions);
        Assert.Equal(1, summary.Resolved);
        Assert.Equal(5, summary.OpenIssues);     // new + recurring + regressed + both buffered (resolved is 2)
        Assert.Equal(2, summary.UsersAffected);
        Assert.True(summary.HadActivity);

        Assert.Equal(4, summary.TopIssues.Count);
        Assert.Equal("Recurring timeout", summary.TopIssues[0].Title);
        Assert.Equal(6, summary.TopIssues[0].Events);

        Assert.Equal(1, summary.Fixes.Proposed);
        Assert.Equal(1, summary.Fixes.PrsOpened);
        Assert.Equal(1, summary.Fixes.PrsMerged);
        Assert.Equal(1, summary.Fixes.AutoResolved);
    }

    // A week whose only activity is an issue that first reached Condux this week, carrying an event dated
    // weeks earlier: a delayed or backdated report. The digest describes what reached Condux in its week, so
    // every section counts it, and the email is sent. Counted by the claimed time instead, the week had no
    // events, so no digest was sent at all, although its new-issue count included the issue.
    [Fact]
    public async Task A_late_event_counts_in_every_section_in_the_week_it_was_received()
    {
        var (orgId, projectId) = await SeedOrgProjectAsync();
        var issues = new IssueRepository(pg.ConnectionString);
        var claimed = FloorHour(DateTimeOffset.UtcNow.AddDays(-20));
        var received = FloorHour(DateTimeOffset.UtcNow.AddDays(-1));

        var late = await issues.UpsertAsync(projectId, Group("fp-late", "Late crash"), Level.Error, claimed);
        await WriteClaimedStatsAsync(projectId, late.Id, [claimed]);
        await WriteStatsAsync(projectId, late.Id, [received]);
        await WriteEventsAsync(projectId, late.Id, claimed, ["user-late"], received);

        using var http = Http();
        var summary = await new WeeklySummaryComposer(
                new ProjectRepository(pg.ConnectionString), issues,
                new ClickHouseIssueStatsReader(http), new ClickHouseEventReader(http),
                new PostgresFixStore(pg.ConnectionString))
            .ComposeAsync(orgId, "Acme", DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.True(summary.HadActivity);
        Assert.Equal(1, summary.Events);
        Assert.Equal(0, summary.PreviousEvents);
        Assert.Equal(1, summary.NewIssues);
        Assert.Equal(1, summary.UsersAffected);
        Assert.Equal("Late crash", Assert.Single(summary.TopIssues).Title);
    }

    [Fact]
    public async Task Compose_returns_no_activity_for_a_dormant_org()
    {
        var (orgId, _) = await SeedOrgProjectAsync();
        using var http = Http();

        var summary = await new WeeklySummaryComposer(
                new ProjectRepository(pg.ConnectionString), new IssueRepository(pg.ConnectionString),
                new ClickHouseIssueStatsReader(http), new ClickHouseEventReader(http),
                new PostgresFixStore(pg.ConnectionString))
            .ComposeAsync(orgId, "Quiet", DateTimeOffset.UtcNow);

        Assert.False(summary.HadActivity);
        Assert.Equal(0, summary.Events);
        Assert.Empty(summary.TopIssues);
    }

    [Fact]
    public async Task Ledger_claims_each_week_exactly_once()
    {
        var (orgId, _) = await SeedOrgProjectAsync();
        var ledger = new PostgresWeeklySummaryLedger(pg.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var week = new DateOnly(2026, 8, 3);

        Assert.True(await ledger.TryClaimAsync(orgId, week, now));   // first claim wins
        Assert.False(await ledger.TryClaimAsync(orgId, week, now));  // same week is already handled
        Assert.True(await ledger.TryClaimAsync(orgId, week.AddDays(7), now)); // next week is a fresh claim

        // Releasing a claim (a transient send failure) lets the same week be re-attempted.
        await ledger.ReleaseAsync(orgId, week);
        Assert.True(await ledger.TryClaimAsync(orgId, week, now));
    }

    private async Task<(long OrgId, long ProjectId)> SeedOrgProjectAsync()
    {
        var org = await new OrgRepository(pg.ConnectionString).CreateAsync($"org-{Guid.NewGuid():N}", "Acme", 0);
        var project = await new ProjectRepository(pg.ConnectionString).CreateAsync(org.Id, "API", "other");
        return (org.Id, project.Id);
    }

    // One succeeded run created this week (proposed + PR opened), one merged this week, and one that merged
    // earlier but was verified as held this week (auto-resolved). Distinct branches isolate the mark-merged.
    private async Task SeedFixesAsync(long issueId, DateTimeOffset baseNow)
    {
        const string repo = "acme/api";
        var thisWeek = baseNow.AddDays(-1);
        var longAgo = baseNow.AddDays(-20);
        var store = new PostgresFixStore(pg.ConnectionString);
        var verification = new PostgresFixVerification(pg.ConnectionString);

        await store.InsertAsync(new FixSuggestion(
            Guid.NewGuid(), issueId, repo, FixStatus.Succeeded, "fake", "m", "b", "pr", "s", thisWeek, thisWeek));

        await store.InsertAsync(new FixSuggestion(
            Guid.NewGuid(), issueId, repo, FixStatus.Succeeded, "fake", "m", "condux/merged", "pr", "s", longAgo, longAgo));
        await verification.MarkMergedAsync(repo, "condux/merged", thisWeek);

        var heldId = Guid.NewGuid();
        await store.InsertAsync(new FixSuggestion(
            heldId, issueId, repo, FixStatus.Succeeded, "fake", "m", "condux/held", "pr", "s", longAgo, longAgo));
        await verification.MarkMergedAsync(repo, "condux/held", baseNow.AddDays(-9)); // merged last week, not this
        await verification.SetVerifyStatusAsync(heldId, VerifyStatus.Held, thisWeek);
    }

    private HttpClient Http()
    {
        var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        return http;
    }

    // Events the consumer received at each of the given times, as it counts them in the receipt rollup.
    private async Task WriteStatsAsync(long projectId, long issueId, IReadOnlyList<DateTimeOffset> at)
    {
        using var http = Http();
        var project = projectId.ToString(CultureInfo.InvariantCulture);
        await new ClickHouseIssueStatsWriter(http).InsertReceivedAsync(
            [.. at.Select(t => ClickHouseIssueStatsWriter.ToReceivedRow(project, (ulong)issueId, t))]);
    }

    // The same events in the rollup the charts read, by the time each claims.
    private async Task WriteClaimedStatsAsync(long projectId, long issueId, IReadOnlyList<DateTimeOffset> at)
    {
        using var http = Http();
        var project = projectId.ToString(CultureInfo.InvariantCulture);
        await new ClickHouseIssueStatsWriter(http).InsertAsync(
            [.. at.Select(t => ClickHouseIssueStatsWriter.ToRow(project, (ulong)issueId, t))]);
    }

    // Stored events claiming at, received at receivedAt (the same
    // instant when not given), one per user key.
    private async Task WriteEventsAsync(
        long projectId, long issueId, DateTimeOffset at, string[] userKeys, DateTimeOffset? receivedAt = null)
    {
        using var http = Http();
        var ts = at.ToUnixTimeMilliseconds();
        var received = (receivedAt ?? at).ToUnixTimeMilliseconds();
        var rows = userKeys.Select(userKey => new EventRow(
            projectId.ToString(CultureInfo.InvariantCulture), (ulong)issueId, Guid.NewGuid().ToString("N"), ts,
            "error", "", "", "", "", "", "boom", "", "", "fp", new Dictionary<string, string>(), "{}", 90,
            userKey, received)).ToList();
        await new ClickHouseEventWriter(http).InsertAsync(rows);
    }

    private async Task PlaceCreatedAtAsync(long issueId, DateTimeOffset createdAt)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE issues SET created_at = @at WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("at", createdAt);
        cmd.Parameters.AddWithValue("id", issueId);
        await cmd.ExecuteNonQueryAsync();
    }

    private static Grouping Group(string fingerprint, string title) => new(fingerprint, title, "run");

    private static DateTimeOffset[] Repeat(DateTimeOffset t, int count) => [.. Enumerable.Repeat(t, count)];

    private static DateTimeOffset FloorHour(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset);
    [Fact]
    public async Task Recipients_exclude_members_who_opted_out()
    {
        var orgs = new OrgRepository(pg.ConnectionString);
        var users = new UserRepository(pg.ConnectionString);
        var members = new OrgMemberRepository(pg.ConnectionString);

        var org = await orgs.CreateAsync($"org-{Guid.NewGuid():N}", "Acme", 0);
        var staying = await users.TryCreateAsync($"stay-{Guid.NewGuid():N}@x.test", "hash")
            ?? throw new InvalidOperationException("seed address was taken");
        var leaving = await users.TryCreateAsync($"leave-{Guid.NewGuid():N}@x.test", "hash")
            ?? throw new InvalidOperationException("seed address was taken");
        await members.AddAsync(org.Id, staying.Id, OrgRole.Member);
        await members.AddAsync(org.Id, leaving.Id, OrgRole.Member);

        // Both receive it by default: the column defaults to false, so the migration changes nothing for
        // anyone who never touches the setting. Asserted rather than assumed, because if the default were
        // wrong this whole feature would silently unsubscribe an entire install.
        var before = await members.WeeklySummaryRecipientsAsync(org.Id);
        Assert.Contains(staying.Email, before);
        Assert.Contains(leaving.Email, before);

        await users.SetWeeklySummaryOptOutAsync(leaving.Id, optOut: true);

        var after = await members.WeeklySummaryRecipientsAsync(org.Id);
        Assert.Contains(staying.Email, after);
        Assert.DoesNotContain(leaving.Email, after);

        // Reversible, and it is the same one query deciding both directions.
        await users.SetWeeklySummaryOptOutAsync(leaving.Id, optOut: false);
        Assert.Contains(leaving.Email, await members.WeeklySummaryRecipientsAsync(org.Id));
    }

    [Fact]
    public async Task Opt_out_is_per_user_not_per_org()
    {
        var orgs = new OrgRepository(pg.ConnectionString);
        var users = new UserRepository(pg.ConnectionString);
        var members = new OrgMemberRepository(pg.ConnectionString);

        var org = await orgs.CreateAsync($"org-{Guid.NewGuid():N}", "Acme", 0);
        var optedOut = await users.TryCreateAsync($"a-{Guid.NewGuid():N}@x.test", "hash")
            ?? throw new InvalidOperationException("seed address was taken");
        await members.AddAsync(org.Id, optedOut.Id, OrgRole.Member);
        await users.SetWeeklySummaryOptOutAsync(optedOut.Id, optOut: true);

        // The point of the feature: one member opting out must not switch the digest off for the org, which
        // is the workaround it replaces.
        var reloaded = await orgs.GetAsync(org.Id);
        Assert.NotNull(reloaded);
        Assert.Empty(await members.WeeklySummaryRecipientsAsync(org.Id));
        Assert.True((await users.GetByIdAsync(optedOut.Id))!.WeeklySummaryOptOut);
    }
}
