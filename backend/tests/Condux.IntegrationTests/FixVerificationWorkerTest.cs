extern alias conductor;
using System.Text.Json;
using conductor::Condux.Conductor;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// End-to-end coverage for the fix-verification watcher (ADR-0019, #122): a real <see cref="VerificationWorker"/>
/// tick reads a merged fix's post-merge occurrences from the live <c>issue_stats_1h</c> rollup and either
/// resolves the issue with evidence (silent window) or records that the fix did not hold (a recurrence).
/// This exercises the whole chain the unit + persistence tests stub over —
/// <see cref="ClickHouseIssueStatsReader.CountSinceAsync"/> → <see cref="FixVerification.Evaluate"/> →
/// issue status + audit — against real Postgres + ClickHouse.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FixVerificationWorkerTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    [Fact]
    public async Task A_silent_window_resolves_the_issue_and_records_fix_verified_evidence()
    {
        var now = DateTimeOffset.UtcNow;
        // Merged well past the 72h window so only the occurrence count decides the outcome.
        var mergedAt = FloorHour(now.AddHours(-80)).AddMinutes(30);
        var (fixId, issue, projectId) = await SeedMergedFixAsync("fp-hold", mergedAt);

        // An occurrence in the merge hour but *before* the merge — the reader skips the partial first
        // hour, so a pre-merge event must not fail the fix.
        await WriteOccurrencesAsync(projectId, issue.Id, mergedAt.AddMinutes(-10));

        await RunTickAsync(now);

        var concluded = await GetFixAsync(fixId);
        Assert.Equal(VerifyStatus.Held, concluded.VerifyStatus);
        Assert.NotNull(concluded.VerifiedAt);
        Assert.Equal(2, await GetIssueStatusAsync(issue.PublicId)); // resolved

        var evidence = await GetLatestAuditDetailAsync(fixId, "fix_verified");
        Assert.NotNull(evidence);
        var doc = JsonDocument.Parse(evidence!).RootElement;
        Assert.Equal(0, doc.GetProperty("occurrences").GetInt64());
        Assert.Equal(72, doc.GetProperty("windowHours").GetInt32());
        Assert.True(doc.GetProperty("resolved").GetBoolean());
    }

    [Fact]
    public async Task A_post_merge_occurrence_keeps_the_issue_open_and_records_fix_did_not_hold()
    {
        var now = DateTimeOffset.UtcNow;
        var mergedAt = FloorHour(now.AddHours(-80)).AddMinutes(30);
        var (fixId, issue, projectId) = await SeedMergedFixAsync("fp-recur", mergedAt);

        // Two occurrences two hours after the merge — a full bucket past the merge hour, so they count.
        await WriteOccurrencesAsync(
            projectId, issue.Id, FloorHour(mergedAt).AddHours(2), FloorHour(mergedAt).AddHours(2));

        await RunTickAsync(now);

        var concluded = await GetFixAsync(fixId);
        Assert.Equal(VerifyStatus.DidNotHold, concluded.VerifyStatus);
        Assert.NotNull(concluded.VerifiedAt);
        Assert.Equal(1, await GetIssueStatusAsync(issue.PublicId)); // still open (unresolved)

        var evidence = await GetLatestAuditDetailAsync(fixId, "fix_did_not_hold");
        Assert.NotNull(evidence);
        Assert.Equal(2, JsonDocument.Parse(evidence!).RootElement.GetProperty("occurrences").GetInt64());
    }

    [Fact]
    public async Task Inside_the_window_with_no_occurrences_the_fix_stays_watching()
    {
        var now = DateTimeOffset.UtcNow;
        // Merged only an hour ago: the window has not elapsed, so a silent issue stays under watch.
        var mergedAt = FloorHour(now).AddHours(-1).AddMinutes(15);
        var (fixId, issue, _) = await SeedMergedFixAsync("fp-watch", mergedAt);

        await RunTickAsync(now);

        var fix = await GetFixAsync(fixId);
        Assert.Equal(VerifyStatus.Watching, fix.VerifyStatus);
        Assert.Null(fix.VerifiedAt);
        Assert.Equal(1, await GetIssueStatusAsync(issue.PublicId));
        Assert.Null(await GetLatestAuditDetailAsync(fixId, "fix_verified"));
    }

    // Stands up an org/project/issue + a succeeded fix whose draft PR merged, flipping it to watching the
    // way the webhook does (MarkMergedAsync on repo + branch). Returns the ids the assertions need.
    private async Task<(Guid FixId, UpsertResult Issue, long ProjectId)> SeedMergedFixAsync(
        string fingerprint, DateTimeOffset mergedAt)
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping(fingerprint, "TypeError: boom", "run"), Level.Error,
            DateTimeOffset.UtcNow);

        var branch = $"condux/fix-{fingerprint}";
        var repo = "acme/api";
        var now = DateTimeOffset.UtcNow;
        var fixId = Guid.NewGuid();
        await new PostgresFixStore(pg.ConnectionString).InsertAsync(new FixSuggestion(
            fixId, issue.Id, repo, FixStatus.Succeeded, "fake", "model", branch,
            $"https://github.com/{repo}/pull/1", "fixed", now, now));

        var flipped = await new PostgresFixVerification(pg.ConnectionString)
            .MarkMergedAsync(repo, branch, mergedAt);
        Assert.Contains(fixId, flipped);
        return (fixId, issue, projectId);
    }

    private async Task RunTickAsync(DateTimeOffset now)
    {
        using var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        var stats = new ClickHouseIssueStatsReader(http);

        var verification = new PostgresFixVerification(pg.ConnectionString);
        var worker = new VerificationWorker(
            new ConfigurationBuilder().Build(),
            NullLogger<VerificationWorker>.Instance,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            verification,
            new IssueRepository(pg.ConnectionString),
            new PostgresFixStore(pg.ConnectionString),
            new ProjectEventNotifier(pg.ConnectionString),
            new Condux.Telemetry.ConduxSelfReporter(null));

        await worker.ConcludeAsync(await verification.ListWatchingAsync(), stats, now, default);
    }

    private async Task WriteOccurrencesAsync(long projectId, long issueId, params DateTimeOffset[] at)
    {
        using var http = new HttpClient();
        ClickHouseRegistration.Configure(http, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        var project = projectId.ToString();
        await new ClickHouseIssueStatsWriter(http).InsertAsync(
            [.. at.Select(t => ClickHouseIssueStatsWriter.ToRow(project, (ulong)issueId, t))]);
    }

    private async Task<FixSuggestion> GetFixAsync(Guid fixId) =>
        (await new PostgresFixStore(pg.ConnectionString).GetAsync(fixId))!;

    // The hourly rollup buckets on the start of the event's UTC hour; floor the test's timestamps the
    // same way so an occurrence lands in the intended bucket regardless of the current minute.
    private static DateTimeOffset FloorHour(DateTimeOffset t) =>
        new(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Offset);

    private async Task<int> GetIssueStatusAsync(Guid publicId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT status FROM issues WHERE public_id = @id", conn);
        cmd.Parameters.AddWithValue("id", publicId);
        return (short)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string?> GetLatestAuditDetailAsync(Guid fixId, string eventName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT detail::text FROM fix_audit WHERE fix_id = @fix AND event = @event "
            + "ORDER BY created_at DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("fix", fixId);
        cmd.Parameters.AddWithValue("event", eventName);
        return await cmd.ExecuteScalarAsync() as string;
    }
}
