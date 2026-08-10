using System.Globalization;
using Condux.Core.WeeklySummaries;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;

namespace Condux.Notifications;

/// <summary>
/// Builds a <see cref="WeeklySummary"/> for an org from the existing stores (ADR-0031). Deliberately a
/// bounded number of queries: one ClickHouse volume aggregate plus one users-affected call per project, and
/// two org-scoped Postgres aggregates — O(projects), never O(issues), heeding Sentry's weekly-report N+1
/// lesson. The volume aggregate returns one row per issue (this week vs last week), so it also yields the top
/// issues without pulling every hourly bucket into memory.
/// </summary>
public sealed class WeeklySummaryComposer(
    ProjectRepository projects,
    IssueRepository issues,
    ClickHouseIssueStatsReader stats,
    ClickHouseEventReader eventReader,
    PostgresFixStore fixStore)
{
    private const int TopIssueCount = 5;

    public async Task<WeeklySummary> ComposeAsync(
        long orgId, string orgName, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var weekStart = asOf.AddDays(-7);
        var previousStart = asOf.AddDays(-14);

        var projectList = await projects.ListByOrgAsync(orgId, cancellationToken);
        if (projectList.Count == 0)
        {
            return new WeeklySummary(
                orgId, orgName, weekStart, asOf, 0, 0, 0, 0, 0, 0, 0, [], WeeklyFixActivity.None);
        }

        long totalEvents = 0, previousEvents = 0, usersAffected = 0;
        var thisWeekByIssue = new Dictionary<long, long>(); // internal issue id (globally unique) -> this-week events

        foreach (var project in projectList)
        {
            var pid = project.Id.ToString(CultureInfo.InvariantCulture);
            foreach (var volume in await stats.WeeklyVolumesAsync(pid, previousStart, weekStart, asOf, cancellationToken))
            {
                totalEvents += volume.ThisWeek;
                previousEvents += volume.PreviousWeek;
                if (volume.ThisWeek > 0)
                {
                    thisWeekByIssue[volume.IssueId] = volume.ThisWeek;
                }
            }
            usersAffected += await eventReader.CountUsersAffectedAsync(pid, weekStart, asOf, cancellationToken);
        }

        var counts = await issues.WeeklyIssueCountsAsync(
            projectList.Select(p => p.Id).ToList(), weekStart, asOf, cancellationToken);
        var fixActivity = await fixStore.WeeklyFixActivityAsync(orgId, weekStart, asOf, cancellationToken);
        var topIssues = await ResolveTopIssuesAsync(thisWeekByIssue, cancellationToken);

        return new WeeklySummary(
            orgId, orgName, weekStart, asOf, totalEvents, previousEvents,
            counts.New, counts.Regressed, counts.Resolved, counts.Open, usersAffected, topIssues, fixActivity);
    }

    // Rank the week's issues by event count and fetch their titles in one query; drops any that no longer
    // exist in Postgres (deleted between the stats read and the title lookup) rather than showing a blank.
    private async Task<IReadOnlyList<TopIssue>> ResolveTopIssuesAsync(
        Dictionary<long, long> thisWeekByIssue, CancellationToken cancellationToken)
    {
        var topIds = thisWeekByIssue
            .OrderByDescending(entry => entry.Value)
            .Take(TopIssueCount)
            .Select(entry => entry.Key)
            .ToList();
        if (topIds.Count == 0)
        {
            return [];
        }

        var summaries = await issues.SummariesByInternalIdsAsync(topIds, cancellationToken);
        return topIds
            .Where(summaries.ContainsKey)
            .Select(id => new TopIssue(summaries[id].Title, thisWeekByIssue[id]))
            .ToList();
    }
}
