using Condux.Core.WeeklySummaries;
using Xunit;

namespace Condux.Core.Tests;

public class WeeklySummaryTextTests
{
    private static WeeklySummary Full() => new(
        OrgId: 1, OrgName: "Acme",
        WeekStart: new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero),
        WeekEnd: new DateTimeOffset(2024, 1, 8, 9, 0, 0, TimeSpan.Zero),
        Events: 18432, PreviousEvents: 15120,
        NewIssues: 12, Regressions: 3, Resolved: 8, OpenIssues: 41, UsersAffected: 1204,
        TopIssues:
        [
            new TopIssue("TypeError: undefined is not a function", 4231),
            new TopIssue("NullReferenceException in Pay", 2890),
        ],
        Fixes: new WeeklyFixActivity(5, 4, 2, 1));

    private static WeeklySummary Quiet() => new(
        OrgId: 2, OrgName: "Solo",
        WeekStart: new DateTimeOffset(2024, 1, 1, 9, 0, 0, TimeSpan.Zero),
        WeekEnd: new DateTimeOffset(2024, 1, 8, 9, 0, 0, TimeSpan.Zero),
        Events: 10, PreviousEvents: 0,
        NewIssues: 1, Regressions: 0, Resolved: 0, OpenIssues: 1, UsersAffected: 0,
        TopIssues: [], Fixes: WeeklyFixActivity.None);

    [Fact]
    public void Subject_NamesTheOrg()
    {
        Assert.Equal("Your Condux week: Acme", WeeklySummaryText.Subject(Full()));
    }

    [Fact]
    public void DateRange_IsInvariantMonthDay()
    {
        Assert.Equal("Jan 1 to Jan 8", WeeklySummaryFormat.DateRange(Full()));
    }

    [Fact]
    public void Metrics_ExposeTheSharedRowsBothViewsRender()
    {
        // The single source of the metric rows — the text body and the HTML facts both render these, so they
        // can never list different numbers or labels.
        var metrics = WeeklySummaryFormat.Metrics(Full());
        Assert.Contains(("New issues", "12"), metrics);
        Assert.Contains(("Users affected", "~1,204"), metrics);

        var fixes = WeeklySummaryFormat.FixMetrics(Full());
        Assert.Equal(4, fixes.Count);
        Assert.Contains(("Draft PRs opened", "4"), fixes);
        Assert.Empty(WeeklySummaryFormat.FixMetrics(Quiet()));
    }

    [Fact]
    public void Body_IncludesEveryStatWithThousandsSeparators()
    {
        var body = WeeklySummaryText.Body(Full(), "https://app.condux.ai/");
        Assert.Contains("Events this week: 18,432 (+22% vs last week)", body);
        Assert.Contains("New issues: 12", body);
        Assert.Contains("Regressions: 3", body);
        Assert.Contains("Resolved: 8", body);
        Assert.Contains("Open issues: 41", body);
        Assert.Contains("Users affected: ~1,204", body);
    }

    [Fact]
    public void Body_IncludesConductorAndTopIssueSections()
    {
        var body = WeeklySummaryText.Body(Full(), "https://app.condux.ai/");
        Assert.Contains("Conductor this week", body);
        Assert.Contains("Fixes proposed: 5", body);
        Assert.Contains("Draft PRs opened: 4", body);
        Assert.Contains("PRs merged: 2", body);
        Assert.Contains("Top issues this week", body);
        Assert.Contains("1. TypeError: undefined is not a function (4,231)", body);
        Assert.Contains("Open your dashboard: https://app.condux.ai/", body);
    }

    [Fact]
    public void Body_OmitsEmptySectionsAndComparison()
    {
        var body = WeeklySummaryText.Body(Quiet());
        Assert.Contains("Events this week: 10", body);
        Assert.DoesNotContain("vs last week", body);
        Assert.DoesNotContain("Conductor this week", body);
        Assert.DoesNotContain("Top issues this week", body);
        Assert.DoesNotContain("Open your dashboard", body);
    }

    [Fact]
    public void ChangeSuffix_SignedPercent_OrEmptyWithoutBaseline()
    {
        Assert.Equal(" (+22% vs last week)", WeeklySummaryFormat.ChangeSuffix(Full()));
        Assert.Equal(string.Empty, WeeklySummaryFormat.ChangeSuffix(Quiet()));

        var down = Full() with { Events = 80, PreviousEvents = 100 };
        Assert.Equal(" (-20% vs last week)", WeeklySummaryFormat.ChangeSuffix(down));
    }

    [Fact]
    public void EventsChangePercent_RoundsAndGuardsZeroBaseline()
    {
        Assert.Equal(50, (Full() with { Events = 150, PreviousEvents = 100 }).EventsChangePercent);
        Assert.Null(Quiet().EventsChangePercent);
    }

    [Fact]
    public void HadActivity_TracksEventVolume()
    {
        Assert.True(Full().HadActivity);
        Assert.False((Quiet() with { Events = 0 }).HadActivity);
    }
}
